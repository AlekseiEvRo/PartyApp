using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Services;

public record BingoConfirmOutcome(bool Success, string Message, object? Data = null);

/// <summary>
/// Бинго: отметки игроков и подтверждение клеток админом.
/// Баллы начисляются только за клетки, которые админ подтвердил как
/// реально случившиеся события; линии считаются по подтверждённым клеткам.
/// </summary>
public class BingoService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly IHubContext<PartyHub> _hub;
    private readonly ILogger<BingoService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public BingoService(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        IHubContext<PartyHub> hub,
        ILogger<BingoService> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Подтверждает клетку и начисляет баллы всем, кто её отметил.</summary>
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

            BingoConfig config = ParseConfig(session.Definition.ConfigJson);
            int totalCells = config.Size * config.Size;

            if (config.Cells.Count < totalCells)
                return new BingoConfirmOutcome(false, $"Бинго не настроено: нужно {totalCells} клеток");

            if (cellIndex < 0 || cellIndex >= totalCells)
                return new BingoConfirmOutcome(false, "Неверная клетка");

            List<int> confirmed = await db.BingoCellConfirmations
                .Where(c => c.SessionId == sessionId)
                .Select(c => c.CellIndex)
                .ToListAsync(ct);

            if (confirmed.Contains(cellIndex))
                return new BingoConfirmOutcome(false, "Клетка уже подтверждена");

            HashSet<int> confirmedAfter = confirmed.Append(cellIndex).ToHashSet();
            HashSet<int> confirmedBefore = confirmed.ToHashSet();

            db.BingoCellConfirmations.Add(new BingoCellConfirmation
            {
                SessionId = sessionId,
                CellIndex = cellIndex
            });
            await db.SaveChangesAsync(ct);

            Dictionary<Guid, HashSet<int>> marksByPlayer = await GetMarksByPlayerAsync(db, sessionId, ct);

            int awardedPlayers = 0;

            foreach ((Guid playerId, HashSet<int> marks) in marksByPlayer)
            {
                if (!marks.Contains(cellIndex))
                    continue;

                int beforeLines = CountLines(marks, confirmedBefore, config.Size);
                int afterLines = CountLines(marks, confirmedAfter, config.Size);
                int newLines = afterLines - beforeLines;
                int points = config.PointsPerCell + newLines * config.LineBonus;

                await _pointsAward.AwardAsync(
                    playerId,
                    points,
                    newLines > 0 ? $"Бинго: линия (+{newLines * config.LineBonus})" : "Бинго: событие подтверждено",
                    ct: ct);

                awardedPlayers++;
            }

            var summary = new
            {
                sessionId,
                cellIndex,
                awardedPlayers,
                confirmedCount = confirmedAfter.Count
            };

            await _hub.Clients.All.SendAsync("BingoCellConfirmed", summary, ct);

            _logger.LogInformation(
                "Bingo cell confirmed: session={SessionId}, cell={CellIndex}, awarded={Awarded}",
                sessionId, cellIndex, awardedPlayers);

            return new BingoConfirmOutcome(true, "Клетка подтверждена", summary);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Данные игрока: отметки, подтверждённые клетки и линии.</summary>
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
        HashSet<int> marks = await GetPlayerMarksAsync(db, sessionId, playerId, ct);
        HashSet<int> confirmed = await GetConfirmedCellsAsync(db, sessionId, ct);

        return new
        {
            markedCells = marks.OrderBy(i => i).ToArray(),
            confirmedCells = marks.Where(confirmed.Contains).OrderBy(i => i).ToArray(),
            lines = CountLines(marks, confirmed, config.Size)
        };
    }

    /// <summary>Живые данные для большого экрана.</summary>
    public async Task<object?> GetLiveStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Dictionary<Guid, HashSet<int>> marksByPlayer = await GetMarksByPlayerAsync(db, sessionId, ct);
        int confirmedCount = await db.BingoCellConfirmations.CountAsync(c => c.SessionId == sessionId, ct);

        return new
        {
            markedCount = marksByPlayer.Values.Sum(m => m.Count),
            playersCount = marksByPlayer.Count,
            confirmedCount
        };
    }

    /// <summary>Состояние для админской сетки: отметки по клеткам и подтверждения.</summary>
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
        List<int> confirmed = await db.BingoCellConfirmations
            .Where(c => c.SessionId == sessionId)
            .OrderBy(c => c.CellIndex)
            .Select(c => c.CellIndex)
            .ToListAsync(ct);

        Dictionary<Guid, HashSet<int>> marksByPlayer = await GetMarksByPlayerAsync(db, sessionId, ct);

        var markCounts = new Dictionary<int, int>();
        foreach (HashSet<int> marks in marksByPlayer.Values)
        {
            foreach (int cell in marks)
                markCounts[cell] = markCounts.GetValueOrDefault(cell) + 1;
        }

        return new
        {
            sessionId,
            displayName = session.Definition.DisplayName,
            size = config.Size,
            cells = config.Cells,
            pointsPerCell = config.PointsPerCell,
            lineBonus = config.LineBonus,
            confirmedCells = confirmed,
            markCounts,
            playersCount = marksByPlayer.Count
        };
    }

    internal static BingoConfig ParseConfig(string configJson)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BingoConfig>(configJson, EventJsonOptions.Default);
            if (parsed is null)
                return new BingoConfig();

            if (parsed.Size is < 3 or > 7)
                parsed.Size = 5;

            if (parsed.PointsPerCell <= 0)
                parsed.PointsPerCell = 1;

            if (parsed.LineBonus <= 0)
                parsed.LineBonus = 10;

            parsed.Cells ??= new List<string>();
            return parsed;
        }
        catch
        {
            return new BingoConfig();
        }
    }

    internal static int CountLines(HashSet<int> marked, HashSet<int> confirmed, int size)
    {
        return AllLines(size).Count(line => line.All(cell => marked.Contains(cell) && confirmed.Contains(cell)));
    }

    private static async Task<HashSet<int>> GetPlayerMarksAsync(
        AppDbContext db, Guid sessionId, Guid playerId, CancellationToken ct)
    {
        List<string> payloads = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .Select(s => s.PayloadJson)
            .ToListAsync(ct);

        return ExtractMarks(payloads);
    }

    private static async Task<Dictionary<Guid, HashSet<int>>> GetMarksByPlayerAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        var rows = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId)
            .Select(s => new { s.PlayerId, s.PayloadJson })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, HashSet<int>>();

        foreach (var row in rows)
        {
            int? cell = ExtractCellIndex(row.PayloadJson);
            if (cell is null)
                continue;

            if (!result.TryGetValue(row.PlayerId, out HashSet<int>? marks))
            {
                marks = new HashSet<int>();
                result[row.PlayerId] = marks;
            }

            marks.Add(cell.Value);
        }

        return result;
    }

    private static async Task<HashSet<int>> GetConfirmedCellsAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        List<int> confirmed = await db.BingoCellConfirmations
            .Where(c => c.SessionId == sessionId)
            .Select(c => c.CellIndex)
            .ToListAsync(ct);

        return confirmed.ToHashSet();
    }

    private static HashSet<int> ExtractMarks(List<string> payloads)
    {
        var marks = new HashSet<int>();

        foreach (string payloadJson in payloads)
        {
            int? cell = ExtractCellIndex(payloadJson);
            if (cell is not null)
                marks.Add(cell.Value);
        }

        return marks;
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

    internal class BingoConfig
    {
        public int Size { get; set; } = 5;
        public int PointsPerCell { get; set; } = 1;
        public int LineBonus { get; set; } = 10;
        public List<string> Cells { get; set; } = new();
    }
}