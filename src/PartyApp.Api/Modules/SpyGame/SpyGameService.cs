using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Push;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.SpyGame;

public class SpyGameService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<PartyHub> _hubContext;
    private readonly IPushNotificationService _push;
    private readonly PointsAwardService _pointsAward;
    private readonly ILogger<SpyGameService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private SpyGameState _state = new();

    // Слова для шпионов (можно расширить или загружать из конфига)
    private static readonly string[] SpyWords = new[]
    {
        "торт", "шампанское", "подарок", "свеча", "салют",
        "гирлянда", "танец", "музыка", "улыбка", "сюрприз",
        "конфетти", "шарик", "фотография", "тост", "веселье"
    };

    public SpyGameService(
        IServiceScopeFactory scopeFactory,
        IHubContext<PartyHub> hubContext,
        IPushNotificationService push,
        PointsAwardService pointsAward,
        ILogger<SpyGameService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _push = push;
        _pointsAward = pointsAward;
        _logger = logger;
    }

    public SpyGameState GetCurrentState() => _state;

    public async Task<SpyGameState> StartGameAsync(List<Guid> playerIds, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Phase == SpyGamePhase.Playing)
                throw new InvalidOperationException("Игра уже запущена");

            if (playerIds.Count < 4)
                throw new InvalidOperationException("Нужно минимум 4 игрока");

            if (playerIds.Distinct().Count() != playerIds.Count)
                throw new InvalidOperationException("Игроки не должны повторяться");

            // Загружаем данные игроков
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = await db.Users.Where(u => playerIds.Contains(u.Id)).ToListAsync(ct);

            if (users.Count != playerIds.Count)
                throw new InvalidOperationException("Не все игроки найдены");

            // Выбираем 2 случайных шпиона (MVP: фиксированная пара)
            var random = new Random();
            var spyCount = 2;
            var spyIndices = Enumerable.Range(0, users.Count).OrderBy(_ => random.Next()).Take(spyCount).ToList();
            var spyUserIds = spyIndices.Select(i => users[i].Id).ToHashSet();

            // Выбираем случайное слово
            var secretWord = SpyWords[random.Next(SpyWords.Length)];

            // Строим роли
            var players = users.Select(u => new SpyPlayerRole
            {
                UserId = u.Id,
                DisplayName = u.DisplayName,
                Role = spyUserIds.Contains(u.Id) ? RoleType.Spy : RoleType.Townsfolk
            }).ToList();

            // Устанавливаем напарников для шпионов
            // Устанавливаем роли в паре шпионов: один рассказчик, один угадчик
            var spies = players.Where(p => p.Role == RoleType.Spy).ToList();
            if (spies.Count == 2)
            {
                // Первый шпион — рассказчик (видит слово, намекает)
                spies[0].PartnerId = spies[1].UserId;
                spies[0].PartnerName = spies[1].DisplayName;
                spies[0].SecretWord = secretWord;   // рассказчик видит слово
                spies[0].IsGuesser = false;

                // Второй шпион — угадчик (не видит слово, вводит его)
                spies[1].PartnerId = spies[0].UserId;
                spies[1].PartnerName = spies[0].DisplayName;
                spies[1].SecretWord = null;          // угадчик НЕ видит слово
                spies[1].IsGuesser = true;
            }

            // Сбрасываем состояние
            _state = new SpyGameState
            {
                SessionId = Guid.NewGuid(),
                Phase = SpyGamePhase.Playing,
                StartedAt = DateTime.UtcNow,
                SecretWord = secretWord,
                Players = players
            };

            _logger.LogInformation(
                "Spy game started. Session={SessionId}, Spies={Spies}, Word={Word}",
                _state.SessionId,
                string.Join(", ", spies.Select(s => s.DisplayName)),
                secretWord);

            foreach (var player in players)
            {
                await _hubContext.Clients.User(player.UserId.ToString()).SendAsync("SpyGameRoleAssigned", new
                {
                    sessionId = _state.SessionId,
                    role = player.Role.ToString(),
                    isGuesser = player.IsGuesser,
                    partnerName = player.PartnerName,
                    secretWord = player.SecretWord,
                    allPlayers = players
                        .Where(p => p.UserId != player.UserId)
                        .Select(p => new { userId = p.UserId, displayName = p.DisplayName })
                        .ToList()
                }, ct);
            }

            // Всем рассылаем уведомление о старте
            await _hubContext.Clients.All.SendAsync("SpyGameStarted", new
            {
                sessionId = _state.SessionId,
                playersCount = players.Count,
                spyCount
            }, ct);

            // Push участникам: роль нужно посмотреть в приложении
            await _push.SendToUsersAsync(
                players.Select(p => p.UserId).ToList(),
                new PushMessage(
                    Title: "🕵 Игра «Шпионаж» началась!",
                    Body: "Открой приложение и посмотри свою роль",
                    Url: "/",
                    Tag: "spy-game"),
                ct);

            return _state;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SubmissionResult> SubmitWordAsync(Guid playerId, string word, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Phase != SpyGamePhase.Playing)
                return SubmissionResult.Fail("Игра не запущена");

            var player = _state.Players.FirstOrDefault(p => p.UserId == playerId);
            if (player is null)
                return SubmissionResult.Fail("Ты не участвуешь в игре");

            if (player.Role != RoleType.Spy)
                return SubmissionResult.Fail("Только шпионы могут вводить слово");

            if (!player.IsGuesser)
                return SubmissionResult.Fail("Ты рассказчик — ты не вводишь слово, а намекаешь напарнику");

            var normalizedWord = word.Trim().ToLowerInvariant();
            var normalizedSecret = _state.SecretWord.ToLowerInvariant();

            if (normalizedWord != normalizedSecret)
                return SubmissionResult.Fail("Неверное слово. Попробуй ещё раз.");

            // Угадчик ввёл правильное слово — шпионы победили!
            return await FinishGameSpiesWonAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SubmissionResult> AccuseAsync(Guid accuserId, Guid accusedId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Phase != SpyGamePhase.Playing)
                return SubmissionResult.Fail("Игра не запущена");

            var accuser = _state.Players.FirstOrDefault(p => p.UserId == accuserId);
            if (accuser is null)
                return SubmissionResult.Fail("Ты не участвуешь в игре");

            if (accuser.Role != RoleType.Townsfolk)
                return SubmissionResult.Fail("Только горожане могут обвинять");

            if (accuser.HasAccused)
                return SubmissionResult.Fail("Ты уже делал обвинение");

            var accused = _state.Players.FirstOrDefault(p => p.UserId == accusedId);
            if (accused is null)
                return SubmissionResult.Fail("Игрок не найден");

            if (accused.UserId == accuserId)
                return SubmissionResult.Fail("Нельзя обвинять себя");

            // Помечаем, что обвинение сделано
            accuser.HasAccused = true;

            if (accused.Role == RoleType.Spy)
            {
                // Победа горожанина!
                return await FinishGameTownWonAsync(accuser, ct);
            }

            // Не угадал — игра продолжается
            return SubmissionResult.Fail($"{accused.DisplayName} — не шпион. У тебя больше нет попыток.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SpyGameResult?> FinishGameAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.Phase != SpyGamePhase.Playing)
                return null;

            return await FinishGameDrawAsync("Игра завершена администратором", ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<SubmissionResult> FinishGameSpiesWonAsync(CancellationToken ct)
    {
        _state.Phase = SpyGamePhase.Finished;
        _state.FinishedAt = DateTime.UtcNow;
        _state.Winner = "spies";

        var points = 50;
        var spyIds = _state.Spies.Select(s => s.UserId).ToList();
        var spyNames = _state.Spies.Select(s => s.DisplayName).ToList();

        // Начисляем баллы обоим шпионам
        foreach (var spyId in spyIds)
        {
            await _pointsAward.AwardAsync(spyId, points, $"Шпионаж: победа (слово: {_state.SecretWord})", ct: ct);
        }

        var result = new SpyGameResult
        {
            Winner = "spies",
            SpyNames = spyNames,
            PointsAwarded = points,
            SecretWord = _state.SecretWord
        };

        _logger.LogInformation("Spy game finished: spies won. Word={Word}", _state.SecretWord);

        await _hubContext.Clients.All.SendAsync("SpyGameFinished", result, ct);

        return SubmissionResult.Ok(points, "Шпионы победили!", result);
    }

    private async Task<SubmissionResult> FinishGameTownWonAsync(SpyPlayerRole winner, CancellationToken ct)
    {
        _state.Phase = SpyGamePhase.Finished;
        _state.FinishedAt = DateTime.UtcNow;
        _state.TownWinnerId = winner.UserId;
        _state.TownWinnerName = winner.DisplayName;
        _state.Winner = "town";

        var points = 50;
        await _pointsAward.AwardAsync(winner.UserId, points, $"Шпионаж: разоблачил шпиона (слово: {_state.SecretWord})", ct: ct);

        var spyNames = _state.Spies.Select(s => s.DisplayName).ToList();

        var result = new SpyGameResult
        {
            Winner = "town",
            SpyNames = spyNames,
            TownWinnerName = winner.DisplayName,
            PointsAwarded = points,
            SecretWord = _state.SecretWord
        };

        await _hubContext.Clients.All.SendAsync("SpyGameFinished", result, ct);

        return SubmissionResult.Ok(points, $"{winner.DisplayName} разоблачил шпиона!", result);
    }

    private async Task<SpyGameResult?> FinishGameDrawAsync(string reason, CancellationToken ct)
    {
        _state.Phase = SpyGamePhase.Finished;
        _state.FinishedAt = DateTime.UtcNow;
        _state.Winner = "draw";

        var spyNames = _state.Spies.Select(s => s.DisplayName).ToList();

        var result = new SpyGameResult
        {
            Winner = "draw",
            SpyNames = spyNames,
            PointsAwarded = 0,
            SecretWord = _state.SecretWord
        };

        await _hubContext.Clients.All.SendAsync("SpyGameFinished", result, ct);

        return result;
    }

    // Общий DTO для SubmissionResult (если ещё не определён глобально)
    public record SubmissionResult(bool Success, int PointsAwarded, string Message, object? Data = null)
    {
        public static SubmissionResult Ok(int points, string message, object? data = null)
            => new(true, points, message, data);
        public static SubmissionResult Fail(string message, object? data = null)
            => new(false, 0, message, data);
    }
}