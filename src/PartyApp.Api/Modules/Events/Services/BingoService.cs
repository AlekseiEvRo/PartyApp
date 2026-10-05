using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Achievements;
using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Services;

public record BingoConfirmOutcome(bool Success, string Message, object? Data = null);

/// <summary>
/// Бинго в два этапа: сначала игроки выбирают предсказания (до блокировки),
/// затем админ фиксирует приём и отмечает, что было, а что нет.
/// Баллы за сбывшееся предсказание получают все, кто выбрал клетку заранее;
/// бонус за линию — самый быстрый (по времени последней отметки).
/// </summary>
public class BingoService
{
    private const int DefaultSize = 5;
    private const int DefaultPointsPerCell = 5;
    private const int DefaultLineBonus = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly IHubContext<PartyHub> _hub;
    private readonly AchievementService _achievements;
    private readonly ILogger<BingoService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public BingoService(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        IHubContext<PartyHub> hub,
        AchievementService achievements,
        ILogger<BingoService> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _hub = hub;
        _achievements = achievements;
        _logger = logger;
    }

    /// <summary>
    /// Подтверждает клетку: баллы за сбывшееся предсказание получают все, кто
    /// отметил её заранее, затем разыгрываются линии, которые закрыла эта клетка.
    /// </summary>
    public async Task<BingoConfirmOutcome> ConfirmCellAsync(
        Guid sessionId,
        int cellIndex,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            EventSession? session = await db.EventSessions
                .Include(s => s.Definition)
                .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null || session.Definition.Type != "bingo")
                return new BingoConfirmOutcome(false, "Сессия бинго не найдена");

            if (!await IsLockedAsync(db, sessionId, ct))
                return new BingoConfirmOutcome(false, "Сначала завершите приём предсказаний");

            BingoConfig config = ParseConfig(session.Definition.ConfigJson);
            int totalCells = config.Size * config.Size;

            if (config.Cells.Count < totalCells)
                return new BingoConfirmOutcome(false, $"Бинго не настроено: нужно {totalCells} клеток");

            if (cellIndex < 0 || cellIndex >= totalCells)
                return new BingoConfirmOutcome(false, "Неверная клетка");

            Dictionary<int, DateTime> confirmations = await GetConfirmationsAsync(db, sessionId, ct);

            if (confirmations.ContainsKey(cellIndex))
                return new BingoConfirmOutcome(false, "Клетка уже подтверждена");

            bool rejected = await db.BingoCellRejections.AsNoTracking()
                .AnyAsync(c => c.SessionId == sessionId && c.CellIndex == cellIndex, ct);

            if (rejected)
                return new BingoConfirmOutcome(false, "Клетка отклонена: событие не состоялось");

            db.BingoCellConfirmations.Add(new BingoCellConfirmation
            {
                SessionId = sessionId,
                CellIndex = cellIndex
            });
            await db.SaveChangesAsync(ct);

            Dictionary<Guid, Dictionary<int, DateTime>> markTimes = await GetMarkTimesByPlayerAsync(db, sessionId, ct);

            int awardedPlayers = 0;

            foreach ((Guid playerId, Dictionary<int, DateTime> marks) in markTimes)
            {
                if (!marks.ContainsKey(cellIndex))
                    continue;

                await _pointsAward.AwardAsync(
                    playerId,
                    config.PointsPerCell,
                    "Бинго: предсказание сбылось",
                    sessionId: sessionId,
                    ct: ct);

                awardedPlayers++;
            }

            List<LineAwardInfo> lineAwards = await AwardPendingLinesAsync(db, sessionId, config, markTimes, ct);

            var summary = new
            {
                sessionId,
                cellIndex,
                awardedPlayers,
                confirmedCount = confirmations.Count + 1,
                lineAwards = lineAwards.Select(a => new { a.LineIndex, a.LineLabel, a.PlayerId, a.Amount })
            };

            await _hub.Clients.All.SendAsync("BingoCellConfirmed", summary, ct);
            await BroadcastLiveAsync(sessionId, ct);

            _logger.LogInformation(
                "Bingo cell confirmed: session={SessionId}, cell={CellIndex}, awarded={Awarded}, lines={Lines}",
                sessionId, cellIndex, awardedPlayers, lineAwards.Count);

            return new BingoConfirmOutcome(true, "Клетка подтверждена", summary);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Отмечает клетку как «событие не состоялось»: предсказания не сбылись.
    /// Выбор игроков уже зафиксирован блокировкой, поэтому слоты не освобождаются.
    /// </summary>
    public async Task<BingoConfirmOutcome> RejectCellAsync(
        Guid sessionId,
        int cellIndex,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            EventSession? session = await db.EventSessions
                .Include(s => s.Definition)
                .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null || session.Definition.Type != "bingo")
                return new BingoConfirmOutcome(false, "Сессия бинго не найдена");

            if (!await IsLockedAsync(db, sessionId, ct))
                return new BingoConfirmOutcome(false, "Сначала завершите приём предсказаний");

            BingoConfig config = ParseConfig(session.Definition.ConfigJson);
            int totalCells = config.Size * config.Size;

            if (config.Cells.Count < totalCells)
                return new BingoConfirmOutcome(false, $"Бинго не настроено: нужно {totalCells} клеток");

            if (cellIndex < 0 || cellIndex >= totalCells)
                return new BingoConfirmOutcome(false, "Неверная клетка");

            bool confirmed = await db.BingoCellConfirmations.AsNoTracking()
                .AnyAsync(c => c.SessionId == sessionId && c.CellIndex == cellIndex, ct);

            if (confirmed)
                return new BingoConfirmOutcome(false, "Клетка уже подтверждена");

            bool rejected = await db.BingoCellRejections.AsNoTracking()
                .AnyAsync(c => c.SessionId == sessionId && c.CellIndex == cellIndex, ct);

            if (rejected)
                return new BingoConfirmOutcome(false, "Клетка уже отклонена");

            db.BingoCellRejections.Add(new BingoCellRejection
            {
                SessionId = sessionId,
                CellIndex = cellIndex
            });
            await db.SaveChangesAsync(ct);

            int rejectedCount = await db.BingoCellRejections.AsNoTracking()
                .CountAsync(c => c.SessionId == sessionId, ct);

            var summary = new
            {
                sessionId,
                cellIndex,
                rejectedCount
            };

            await _hub.Clients.All.SendAsync("BingoCellRejected", summary, ct);
            await BroadcastLiveAsync(sessionId, ct);

            _logger.LogInformation(
                "Bingo cell rejected: session={SessionId}, cell={CellIndex}",
                sessionId, cellIndex);

            return new BingoConfirmOutcome(true, "Клетка отклонена", summary);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Завершает 1 этап: закрывает приём предсказаний, фиксируя выбор игроков.
    /// После блокировки админ ставит клеткам статусы «было»/«не было».
    /// </summary>
    public async Task<BingoConfirmOutcome> LockAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            EventSession? session = await db.EventSessions
                .Include(s => s.Definition)
                .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null || session.Definition.Type != "bingo")
                return new BingoConfirmOutcome(false, "Сессия бинго не найдена");

            if (await IsLockedAsync(db, sessionId, ct))
                return new BingoConfirmOutcome(false, "Приём предсказаний уже закрыт");

            DateTime lockedAt = DateTime.UtcNow;
            db.BingoLocks.Add(new BingoLock
            {
                SessionId = sessionId,
                LockedAt = lockedAt
            });
            await db.SaveChangesAsync(ct);

            await BroadcastLockAsync(sessionId, true, lockedAt, ct);
            await BroadcastLiveAsync(sessionId, ct);

            _logger.LogInformation("Bingo answers locked: session={SessionId}", sessionId);

            return new BingoConfirmOutcome(true, "Приём предсказаний закрыт", new
            {
                sessionId,
                locked = true,
                lockedAt
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Возвращает 1 этап: снова открывает приём предсказаний. Доступно, только
    /// пока по клеткам нет ни одного решения — иначе выбор игроков уже сыграл.
    /// </summary>
    public async Task<BingoConfirmOutcome> UnlockAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            EventSession? session = await db.EventSessions
                .Include(s => s.Definition)
                .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null || session.Definition.Type != "bingo")
                return new BingoConfirmOutcome(false, "Сессия бинго не найдена");

            BingoLock? bingoLock = await db.BingoLocks
                .SingleOrDefaultAsync(l => l.SessionId == sessionId, ct);

            if (bingoLock is null)
                return new BingoConfirmOutcome(false, "Приём предсказаний ещё не закрыт");

            bool hasDecisions = await db.BingoCellConfirmations.AsNoTracking()
                .AnyAsync(c => c.SessionId == sessionId, ct)
                || await db.BingoCellRejections.AsNoTracking()
                    .AnyAsync(c => c.SessionId == sessionId, ct);

            if (hasDecisions)
                return new BingoConfirmOutcome(false, "Уже есть решения по клеткам — вернуть приём нельзя");

            db.BingoLocks.Remove(bingoLock);
            await db.SaveChangesAsync(ct);

            await BroadcastLockAsync(sessionId, false, null, ct);
            await BroadcastLiveAsync(sessionId, ct);

            _logger.LogInformation("Bingo answers unlocked: session={SessionId}", sessionId);

            return new BingoConfirmOutcome(true, "Приём предсказаний снова открыт", new
            {
                sessionId,
                locked = false,
                lockedAt = (DateTime?)null
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Снимает выбор игрока до блокировки приёма: удаляет его отметку клетки.
    /// После блокировки выбор фиксируется и изменить его нельзя.
    /// </summary>
    public async Task<BingoConfirmOutcome> RemoveMarkAsync(
        Guid sessionId,
        Guid playerId,
        int cellIndex,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            EventSession? session = await db.EventSessions
                .Include(s => s.Definition)
                .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null || session.Definition.Type != "bingo")
                return new BingoConfirmOutcome(false, "Сессия бинго не найдена");

            BingoConfig config = ParseConfig(session.Definition.ConfigJson);
            int totalCells = config.Size * config.Size;

            if (cellIndex < 0 || cellIndex >= totalCells)
                return new BingoConfirmOutcome(false, "Неверная клетка");

            if (await IsLockedAsync(db, sessionId, ct))
                return new BingoConfirmOutcome(false, "Приём предсказаний закрыт — выбор больше не изменить");

            List<PlayerSubmission> submissions = await db.PlayerSubmissions
                .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
                .OrderBy(s => s.SubmittedAt)
                .ThenBy(s => s.Id)
                .ToListAsync(ct);

            PlayerSubmission? mark = submissions
                .FirstOrDefault(s => ExtractCellIndex(s.PayloadJson) == cellIndex);

            if (mark is null)
                return new BingoConfirmOutcome(false, "Эта клетка не отмечена");

            db.PlayerSubmissions.Remove(mark);
            await db.SaveChangesAsync(ct);

            var selected = new HashSet<int>();

            foreach (PlayerSubmission submission in submissions)
            {
                if (ReferenceEquals(submission, mark))
                    continue;

                int? cell = ExtractCellIndex(submission.PayloadJson);
                if (cell is not null)
                    selected.Add(cell.Value);
            }

            await BroadcastLiveAsync(sessionId, ct);

            return new BingoConfirmOutcome(true, "Выбор снят", new
            {
                sessionId,
                cellIndex,
                markedCells = selected.OrderBy(i => i).ToArray(),
                selectedCount = selected.Count,
                maxPredictions = config.MaxPredictions
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Вызывается после успешной отметки игрока: вдруг она закрыла линию,
    /// все клетки которой уже подтверждены. Сбой сюда не должен ломать сабмит.
    /// </summary>
    public async Task CheckLineAwardsAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            EventSession? session = await db.EventSessions
                .Include(s => s.Definition)
                .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null || session.Definition.Type != "bingo")
                return;

            BingoConfig config = ParseConfig(session.Definition.ConfigJson);
            int totalCells = config.Size * config.Size;

            if (config.Cells.Count < totalCells)
                return;

            Dictionary<Guid, Dictionary<int, DateTime>> markTimes = await GetMarkTimesByPlayerAsync(db, sessionId, ct);
            List<LineAwardInfo> awards = await AwardPendingLinesAsync(db, sessionId, config, markTimes, ct);

            if (awards.Count == 0)
                return;

            await _hub.Clients.All.SendAsync("BingoLineAwarded", new
            {
                sessionId,
                lineAwards = awards.Select(a => new { a.LineIndex, a.LineLabel, a.PlayerId, a.Amount })
            }, ct);

            await BroadcastLiveAsync(sessionId, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Данные игрока: выбор, решения админа, блокировка приёма и линии.</summary>
    public async Task<object?> GetPlayerStateAsync(Guid sessionId, Guid playerId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EventDefinition? definition = await db.EventSessions
            .Where(s => s.Id == sessionId)
            .Select(s => s.Definition)
            .FirstOrDefaultAsync(ct);

        if (definition is null)
            return null;

        BingoConfig config = ParseConfig(definition.ConfigJson);
        Dictionary<int, DateTime> marks = await GetPlayerMarkTimesAsync(db, sessionId, playerId, ct);
        Dictionary<int, DateTime> confirmations = await GetConfirmationsAsync(db, sessionId, ct);
        HashSet<int> rejected = await GetRejectedCellsAsync(db, sessionId, ct);
        DateTime? lockedAt = await GetLockedAtAsync(db, sessionId, ct);

        var pending = new List<int>();
        var predictions = new List<int>();

        foreach (int cell in marks.Keys)
        {
            if (rejected.Contains(cell))
                continue;

            if (confirmations.ContainsKey(cell))
                predictions.Add(cell);
            else
                pending.Add(cell);
        }

        int wonLines = await db.BingoLineAwards.AsNoTracking()
            .CountAsync(a => a.SessionId == sessionId && a.PlayerId == playerId, ct);

        return new
        {
            locked = lockedAt.HasValue,
            lockedAt,
            markedCells = marks.Keys.OrderBy(i => i).ToArray(),
            pendingCells = pending.OrderBy(i => i).ToArray(),
            predictionCells = predictions.OrderBy(i => i).ToArray(),
            // Совместимость со старым клиентом: сбывшиеся предсказания = подтверждённые клетки
            confirmedCells = predictions.OrderBy(i => i).ToArray(),
            allConfirmedCells = confirmations.Keys.OrderBy(i => i).ToArray(),
            rejectedCells = rejected.OrderBy(i => i).ToArray(),
            selectedCount = marks.Count,
            pendingCount = pending.Count,
            maxPredictions = config.MaxPredictions,
            pointsPerCell = config.PointsPerCell,
            lineBonus = config.LineBonus,
            lines = CountLines(marks.Keys.ToHashSet(), confirmations.Keys.ToHashSet(), config.Size),
            wonLines
        };
    }

    /// <summary>Живые данные большого экрана: сетка, решения админа и счётчики.</summary>
    public async Task<object?> GetLiveStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EventSession? session = await db.EventSessions
            .Include(s => s.Definition)
            .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null || session.Definition.Type != "bingo")
            return null;

        BingoConfig config = ParseConfig(session.Definition.ConfigJson);
        int totalCells = config.Size * config.Size;

        // Сетка не настроена — рисовать нечего
        List<string> cells = config.Cells.Count >= totalCells ? config.Cells : new List<string>();

        Dictionary<Guid, Dictionary<int, DateTime>> marksByPlayer = await GetMarkTimesByPlayerAsync(db, sessionId, ct);
        HashSet<int> confirmed = await GetConfirmedCellsAsync(db, sessionId, ct);
        HashSet<int> rejected = await GetRejectedCellsAsync(db, sessionId, ct);
        DateTime? lockedAt = await GetLockedAtAsync(db, sessionId, ct);
        int awardedLines = await db.BingoLineAwards.CountAsync(a => a.SessionId == sessionId, ct);

        return new
        {
            size = config.Size,
            cells,
            locked = lockedAt.HasValue,
            lockedAt,
            maxPredictions = config.MaxPredictions,
            confirmedCells = confirmed.OrderBy(i => i).ToArray(),
            rejectedCells = rejected.OrderBy(i => i).ToArray(),
            markedCount = marksByPlayer.Values.Sum(marks => marks.Count),
            playersCount = marksByPlayer.Count,
            confirmedCount = confirmed.Count,
            rejectedCount = rejected.Count,
            awardedLines
        };
    }

    /// <summary>Состояние для админской сетки: отметки по клеткам, подтверждения и линии.</summary>
    public async Task<object?> GetAdminStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EventSession? session = await db.EventSessions
            .Include(s => s.Definition)
            .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null || session.Definition.Type != "bingo")
            return null;

        BingoConfig config = ParseConfig(session.Definition.ConfigJson);
        Dictionary<int, DateTime> confirmations = await GetConfirmationsAsync(db, sessionId, ct);
        HashSet<int> rejected = await GetRejectedCellsAsync(db, sessionId, ct);
        Dictionary<Guid, Dictionary<int, DateTime>> marksByPlayer = await GetMarkTimesByPlayerAsync(db, sessionId, ct);
        DateTime? lockedAt = await GetLockedAtAsync(db, sessionId, ct);

        var markCounts = new Dictionary<int, int>();

        foreach (Dictionary<int, DateTime> marks in marksByPlayer.Values)
        {
            foreach (int cell in marks.Keys)
                markCounts[cell] = markCounts.GetValueOrDefault(cell) + 1;
        }

        var awards = await db.BingoLineAwards.AsNoTracking()
            .Where(a => a.SessionId == sessionId)
            .OrderBy(a => a.LineIndex)
            .Select(a => new
            {
                a.LineIndex,
                a.PlayerId,
                PlayerName = a.Player.DisplayName,
                a.Amount,
                a.AwardedAt
            })
            .ToListAsync(ct);

        var lineAwards = awards
            .Select(a => new
            {
                a.LineIndex,
                LineLabel = LineLabel(config.Size, a.LineIndex),
                a.PlayerId,
                a.PlayerName,
                a.Amount,
                a.AwardedAt
            })
            .ToList();

        return new
        {
            sessionId,
            displayName = session.Definition.DisplayName,
            size = config.Size,
            cells = config.Cells,
            locked = lockedAt.HasValue,
            lockedAt,
            pointsPerCell = config.PointsPerCell,
            lineBonus = config.LineBonus,
            maxPredictions = config.MaxPredictions,
            confirmedCells = confirmations.Keys.OrderBy(i => i).ToArray(),
            rejectedCells = rejected.OrderBy(i => i).ToArray(),
            markCounts,
            pickedPredictions = marksByPlayer.Values.Sum(marks => marks.Count),
            playersCount = marksByPlayer.Count,
            lineAwards
        };
    }

    internal static BingoConfig ParseConfig(string configJson)
    {
        BingoConfig parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<BingoConfig>(configJson, EventJsonOptions.Default)
                ?? new BingoConfig();
        }
        catch
        {
            parsed = new BingoConfig();
        }

        if (parsed.Size is < 3 or > 7)
            parsed.Size = DefaultSize;

        int totalCells = parsed.Size * parsed.Size;
        // Не больше половины поля, с округлением вверх: 5×5 → 13
        int maxPredictions = Math.Max(1, (totalCells + 1) / 2);

        if (parsed.PointsPerCell <= 0)
            parsed.PointsPerCell = DefaultPointsPerCell;

        if (parsed.LineBonus <= 0)
            parsed.LineBonus = DefaultLineBonus;

        // Выбор фиксирован и не превышает половину поля: слоты больше не освобождаются
        if (parsed.MaxPredictions <= 0 || parsed.MaxPredictions > maxPredictions)
            parsed.MaxPredictions = maxPredictions;

        parsed.Cells ??= new List<string>();

        // Лишние клетки за пределами игрового поля отбрасываем: админка и игроки
        // должны видеть одну и ту же сетку
        if (parsed.Cells.Count > totalCells)
            parsed.Cells = parsed.Cells.Take(totalCells).ToList();

        return parsed;
    }

    internal static int CountLines(HashSet<int> marked, HashSet<int> confirmed, int size)
    {
        return AllLines(size).Count(line => line.All(cell => marked.Contains(cell) && confirmed.Contains(cell)));
    }

    internal static IEnumerable<(int Index, int[] Cells)> IndexedLines(int size)
    {
        int index = 0;

        foreach (int[] cells in AllLines(size))
        {
            yield return (index, cells);
            index++;
        }
    }

    internal static string LineLabel(int size, int lineIndex)
    {
        if (lineIndex < size)
            return $"Ряд {lineIndex + 1}";

        if (lineIndex < 2 * size)
            return $"Столбец {lineIndex - size + 1}";

        return lineIndex == 2 * size ? "Диагональ ↘" : "Диагональ ↗";
    }

    /// <summary>
    /// Разыгрывает линии, которые уже полностью подтверждены и ещё не разыграны.
    /// Победитель — игрок с минимальным временем последней отметки линии.
    /// </summary>
    private async Task<List<LineAwardInfo>> AwardPendingLinesAsync(
        AppDbContext db,
        Guid sessionId,
        BingoConfig config,
        Dictionary<Guid, Dictionary<int, DateTime>> markTimes,
        CancellationToken ct)
    {
        HashSet<int> confirmed = await GetConfirmedCellsAsync(db, sessionId, ct);

        List<int> awarded = await db.BingoLineAwards.AsNoTracking()
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.LineIndex)
            .ToListAsync(ct);

        HashSet<int> awardedLines = awarded.ToHashSet();
        var result = new List<LineAwardInfo>();

        foreach ((int lineIndex, int[] cells) in IndexedLines(config.Size))
        {
            if (awardedLines.Contains(lineIndex))
                continue;

            if (!cells.All(confirmed.Contains))
                continue;

            Guid? winner = null;
            DateTime winnerCompletedAt = DateTime.MaxValue;

            foreach ((Guid playerId, Dictionary<int, DateTime> marks) in markTimes)
            {
                if (!cells.All(marks.ContainsKey))
                    continue;

                DateTime completedAt = cells.Max(cell => marks[cell]);

                bool better = winner is null
                    || completedAt < winnerCompletedAt
                    || (completedAt == winnerCompletedAt && playerId.CompareTo(winner.Value) < 0);

                if (!better)
                    continue;

                winner = playerId;
                winnerCompletedAt = completedAt;
            }

            if (winner is null)
                continue;

            db.BingoLineAwards.Add(new BingoLineAward
            {
                SessionId = sessionId,
                LineIndex = lineIndex,
                PlayerId = winner.Value,
                Amount = config.LineBonus
            });
            await db.SaveChangesAsync(ct);

            await _pointsAward.AwardAsync(
                winner.Value,
                config.LineBonus,
                $"Бинго: линия «{LineLabel(config.Size, lineIndex)}» — самый быстрый (+{config.LineBonus})",
                sessionId: sessionId,
                ct: ct);

            await _achievements.OnBingoLineAsync(winner.Value, ct);

            result.Add(new LineAwardInfo(lineIndex, LineLabel(config.Size, lineIndex), winner.Value, config.LineBonus));

            _logger.LogInformation(
                "Bingo line awarded: session={SessionId}, line={LineIndex}, player={PlayerId}, amount={Amount}",
                sessionId, lineIndex, winner, config.LineBonus);
        }

        return result;
    }

    private static async Task<Dictionary<Guid, Dictionary<int, DateTime>>> GetMarkTimesByPlayerAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        var rows = await db.PlayerSubmissions.AsNoTracking()
            .Where(s => s.SessionId == sessionId)
            .OrderBy(s => s.SubmittedAt)
            .ThenBy(s => s.Id)
            .Select(s => new { s.PlayerId, s.PayloadJson, s.SubmittedAt })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, Dictionary<int, DateTime>>();

        foreach (var row in rows)
        {
            int? cell = ExtractCellIndex(row.PayloadJson);
            if (cell is null)
                continue;

            if (!result.TryGetValue(row.PlayerId, out Dictionary<int, DateTime>? marks))
            {
                marks = new Dictionary<int, DateTime>();
                result[row.PlayerId] = marks;
            }

            // Самая ранняя отметка клетки — та, что считалась предсказанием
            if (!marks.ContainsKey(cell.Value))
                marks[cell.Value] = row.SubmittedAt;
        }

        return result;
    }

    /// <summary>Рассылает экранам свежие «живые» данные бинго: сетку и решения админа.</summary>
    private async Task BroadcastLiveAsync(Guid sessionId, CancellationToken ct)
    {
        object? live = await GetLiveStateAsync(sessionId, ct);

        if (live is null)
            return;

        await _hub.Clients.All.SendAsync("EventLiveUpdated", new
        {
            sessionId,
            live
        }, ct);
    }

    /// <summary>Сообщает карточкам игроков о закрытии или возврате приёма предсказаний.</summary>
    private Task BroadcastLockAsync(Guid sessionId, bool locked, DateTime? lockedAt, CancellationToken ct)
    {
        return _hub.Clients.All.SendAsync("BingoLocked", new
        {
            sessionId,
            locked,
            lockedAt
        }, ct);
    }

    internal static async Task<Dictionary<int, DateTime>> GetPlayerMarkTimesAsync(
        AppDbContext db, Guid sessionId, Guid playerId, CancellationToken ct)
    {
        var rows = await db.PlayerSubmissions.AsNoTracking()
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .OrderBy(s => s.SubmittedAt)
            .ThenBy(s => s.Id)
            .Select(s => new { s.PayloadJson, s.SubmittedAt })
            .ToListAsync(ct);

        var marks = new Dictionary<int, DateTime>();

        foreach (var row in rows)
        {
            int? cell = ExtractCellIndex(row.PayloadJson);
            if (cell is not null && !marks.ContainsKey(cell.Value))
                marks[cell.Value] = row.SubmittedAt;
        }

        return marks;
    }

    private static async Task<Dictionary<int, DateTime>> GetConfirmationsAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        var rows = await db.BingoCellConfirmations.AsNoTracking()
            .Where(c => c.SessionId == sessionId)
            .Select(c => new { c.CellIndex, c.ConfirmedAt })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.CellIndex, r => r.ConfirmedAt);
    }

    internal static async Task<HashSet<int>> GetConfirmedCellsAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        List<int> confirmed = await db.BingoCellConfirmations.AsNoTracking()
            .Where(c => c.SessionId == sessionId)
            .Select(c => c.CellIndex)
            .ToListAsync(ct);

        return confirmed.ToHashSet();
    }

    internal static async Task<HashSet<int>> GetRejectedCellsAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        List<int> rejected = await db.BingoCellRejections.AsNoTracking()
            .Where(c => c.SessionId == sessionId)
            .Select(c => c.CellIndex)
            .ToListAsync(ct);

        return rejected.ToHashSet();
    }

    /// <summary>Приём предсказаний закрыт админом (начался 2 этап).</summary>
    internal static async Task<bool> IsLockedAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        return await GetLockedAtAsync(db, sessionId, ct) is not null;
    }

    /// <summary>Когда админ закрыл приём предсказаний; null — приём ещё открыт.</summary>
    internal static async Task<DateTime?> GetLockedAtAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        return await db.BingoLocks.AsNoTracking()
            .Where(l => l.SessionId == sessionId)
            .Select(l => (DateTime?)l.LockedAt)
            .FirstOrDefaultAsync(ct);
    }

    internal static int? ExtractCellIndex(string payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
            if (payload.TryGetProperty("cellIndex", out var indexProp) && indexProp.TryGetInt32(out int index))
                return index;
        }
        catch { /* ignore */ }

        return null;
    }

    private static IEnumerable<int[]> AllLines(int size)
    {
        for (int row = 0; row < size; row++)
            yield return Enumerable.Range(row * size, size).ToArray();

        for (int col = 0; col < size; col++)
            yield return Enumerable.Range(0, size).Select(row => row * size + col).ToArray();

        yield return Enumerable.Range(0, size).Select(i => i * size + i).ToArray();
        yield return Enumerable.Range(0, size).Select(i => i * size + (size - 1 - i)).ToArray();
    }

    internal sealed record LineAwardInfo(int LineIndex, string LineLabel, Guid PlayerId, int Amount);

    internal class BingoConfig
    {
        public int Size { get; set; } = DefaultSize;
        public int PointsPerCell { get; set; } = DefaultPointsPerCell;
        public int LineBonus { get; set; } = DefaultLineBonus;
        public int MaxPredictions { get; set; }
        public List<string> Cells { get; set; } = new();
    }
}