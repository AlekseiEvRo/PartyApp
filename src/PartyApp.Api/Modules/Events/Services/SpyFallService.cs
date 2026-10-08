using System.Text.Json;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Push;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Services;

public record SpyFallOutcome(bool Success, string Message, object? Data = null, int PointsAwarded = 0);

/// <summary>
/// Игра «Шпионы»: админ выбирает участников, сервер выдаёт горожанам одно слово,
/// шпиону — похожее слово из той же темы. Горожане тайно голосуют (голос можно
/// менять, пока не проголосовали все), шпион может один раз угадать слово.
/// Побеждает сторона, набравшая больше голосов: горожане — если у шпиона строго
/// больше голосов, чем у любого другого, иначе победа шпиона. Баллы из конфига:
/// шпиону за победу, горожанам — только за верный голос.
/// </summary>
public class SpyFallService
{
    public const string EventType = "spyfall";
    public const int MinPlayers = 4;

    private const int DefaultPointsSpy = 50;
    private const int DefaultPointsCitizen = 25;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly IHubContext<PartyHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly ILogger<SpyFallService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public SpyFallService(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        IHubContext<PartyHub> hub,
        IPushNotificationService push,
        ILogger<SpyFallService> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _hub = hub;
        _push = push;
        _logger = logger;
    }

    /// <summary>Пара слов: что видят горожане и что видит шпион.</summary>
    public sealed record SpyFallPair(string Citizen, string Spy);

    public sealed record SpyFallConfig(int PointsSpy, int PointsCitizen, List<SpyFallPair> Pairs);

    /// <summary>
    /// Запускает игру: проверяет состав, выдаёт роли и слова, рассылает EventStarted
    /// только выбранным участникам (в payload есть participantIds).
    /// </summary>
    public async Task<SpyFallOutcome> StartAsync(
        Guid definitionId,
        Guid startedById,
        IReadOnlyList<Guid> playerIds,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (playerIds.Count != playerIds.Distinct().Count())
                return new SpyFallOutcome(false, "Игроки не должны повторяться");

            if (playerIds.Count < MinPlayers)
                return new SpyFallOutcome(false, $"Нужно минимум {MinPlayers} игрока");

            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            EventDefinition? definition = await db.EventDefinitions.FindAsync(new object[] { definitionId }, ct);
            if (definition is null || definition.Type != EventType)
                return new SpyFallOutcome(false, "Ивент «Шпионы» не найден");

            if (!definition.IsActive)
                return new SpyFallOutcome(false, "Ивент деактивирован");

            bool hasActive = await db.EventSessions
                .AnyAsync(s => s.State == EventSessionState.Active && s.Definition.Type == EventType, ct);
            if (hasActive)
                return new SpyFallOutcome(false, "Игра уже запущена — сначала завершите текущую");

            SpyFallConfig config = ParseConfig(definition.ConfigJson);
            if (config.Pairs.Count < 3)
                return new SpyFallOutcome(false, "В конфиге ивента нужно минимум 3 пары слов");

            List<User> users = await db.Users
                .Where(u => playerIds.Contains(u.Id))
                .ToListAsync(ct);

            if (users.Count != playerIds.Count)
                return new SpyFallOutcome(false, "Не все игроки найдены");

            if (users.Any(u => u.Role != UserRole.Player))
                return new SpyFallOutcome(false, "В игре могут участвовать только игроки");

            Random random = Random.Shared;

            // Случайная пара и случайная сторона пары: шпион не всегда видит «второе» слово
            SpyFallPair pair = config.Pairs[random.Next(config.Pairs.Count)];
            bool swap = random.Next(2) == 0;
            string citizenWord = swap ? pair.Spy : pair.Citizen;
            string spyWord = swap ? pair.Citizen : pair.Spy;

            User spy = users[random.Next(users.Count)];

            DateTime now = DateTime.UtcNow;
            var session = new EventSession
            {
                DefinitionId = definition.Id,
                StartedById = startedById,
                State = EventSessionState.Active,
                StartedAt = now
            };

            if (definition.DurationMinutes is > 0)
                session.EndsAt = now.AddMinutes(definition.DurationMinutes.Value);

            db.EventSessions.Add(session);

            db.SpyFallRounds.Add(new SpyFallRound
            {
                Session = session,
                SpyUserId = spy.Id,
                CitizenWord = citizenWord,
                SpyWord = spyWord
            });

            foreach (User user in users)
            {
                db.SpyFallParticipants.Add(new SpyFallParticipant
                {
                    Session = session,
                    UserId = user.Id,
                    IsSpy = user.Id == spy.Id
                });
            }

            await db.SaveChangesAsync(ct);

            List<Guid> participantIds = users.Select(u => u.Id).ToList();

            _logger.LogInformation(
                "SpyFall started: session={SessionId}, players={Players}, spy={SpyUserId}, word={CitizenWord}",
                session.Id, users.Count, spy.Id, citizenWord);

            await _hub.Clients.All.SendAsync("EventStarted", new
            {
                sessionId = session.Id,
                definitionId = definition.Id,
                type = definition.Type,
                displayName = definition.DisplayName,
                description = definition.Description,
                availability = definition.Availability.ToString(),
                startedAt = session.StartedAt,
                endsAt = session.EndsAt,
                // Скрытая игра: клиенты не из списка не показывают карточку
                participantIds
            }, ct);

            await _push.SendToUsersAsync(
                participantIds,
                new PushMessage(
                    Title: "🕵 Игра «Шпионы» началась!",
                    Body: "Открой приложение и посмотри своё слово",
                    Url: "/",
                    Tag: $"spyfall-{session.Id}"),
                ct);

            return new SpyFallOutcome(true, "Игра запущена", new
            {
                sessionId = session.Id,
                playersCount = users.Count,
                startedAt = session.StartedAt,
                endsAt = session.EndsAt
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Голос горожанина за подозреваемого. Голос можно менять до последнего голоса.</summary>
    public async Task<SpyFallOutcome> VoteAsync(
        Guid sessionId,
        Guid playerId,
        Guid targetId,
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

            if (session is null || session.Definition.Type != EventType)
                return new SpyFallOutcome(false, "Игра не найдена");

            if (session.State != EventSessionState.Active)
                return new SpyFallOutcome(false, "Игра уже завершена");

            SpyFallRound? round = await db.SpyFallRounds
                .SingleOrDefaultAsync(r => r.SessionId == sessionId, ct);

            if (round is null)
                return new SpyFallOutcome(false, "Раунд не найден");

            if (round.ResolvedAt is not null)
                return new SpyFallOutcome(false, "Игра уже завершена");

            List<SpyFallParticipant> participants = await db.SpyFallParticipants
                .Where(p => p.SessionId == sessionId)
                .ToListAsync(ct);

            SpyFallParticipant? me = participants.FirstOrDefault(p => p.UserId == playerId);
            if (me is null)
                return new SpyFallOutcome(false, "Ты не участвуешь в игре");

            if (me.IsSpy)
                return new SpyFallOutcome(false, "Шпион не голосует — он может только угадать слово");

            if (targetId == playerId)
                return new SpyFallOutcome(false, "Нельзя голосовать за себя");

            if (participants.All(p => p.UserId != targetId))
                return new SpyFallOutcome(false, "Этот игрок не участвует в игре");

            bool changed = me.VotedForId is not null;
            me.VotedForId = targetId;
            me.VotedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            int citizensCount = participants.Count(p => !p.IsSpy);
            int votedCount = participants.Count(p => !p.IsSpy && p.VotedForId is not null);

            _logger.LogInformation(
                "SpyFall vote: session={SessionId}, player={PlayerId}, changed={Changed}, voted={Voted}/{Total}",
                sessionId, playerId, changed, votedCount, citizensCount);

            if (votedCount == citizensCount)
                await ResolveByVotesAsync(db, session, round, participants, ct);
            else
                await BroadcastLiveAsync(sessionId, ct);

            return new SpyFallOutcome(
                true,
                changed ? "Голос изменён" : "Голос принят",
                await GetPlayerStateAsync(sessionId, playerId, ct));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Единственная попытка шпиона угадать слово горожан.</summary>
    public async Task<SpyFallOutcome> GuessAsync(
        Guid sessionId,
        Guid playerId,
        string word,
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

            if (session is null || session.Definition.Type != EventType)
                return new SpyFallOutcome(false, "Игра не найдена");

            if (session.State != EventSessionState.Active)
                return new SpyFallOutcome(false, "Игра уже завершена");

            SpyFallRound? round = await db.SpyFallRounds
                .SingleOrDefaultAsync(r => r.SessionId == sessionId, ct);

            if (round is null)
                return new SpyFallOutcome(false, "Раунд не найден");

            if (round.ResolvedAt is not null)
                return new SpyFallOutcome(false, "Игра уже завершена");

            SpyFallParticipant? me = await db.SpyFallParticipants
                .SingleOrDefaultAsync(p => p.SessionId == sessionId && p.UserId == playerId, ct);

            if (me is null)
                return new SpyFallOutcome(false, "Ты не участвуешь в игре");

            if (!me.IsSpy)
                return new SpyFallOutcome(false, "Угадать слово может только шпион");

            if (round.GuessUsedAt is not null)
                return new SpyFallOutcome(false, "Попытка уже использована");

            string answer = word.Trim();
            if (answer.Length == 0)
                return new SpyFallOutcome(false, "Введи слово");

            if (answer.Length > 50)
                return new SpyFallOutcome(false, "Слишком длинное слово");

            SpyFallConfig config = ParseConfig(session.Definition.ConfigJson);
            bool correct = Normalize(answer) == Normalize(round.CitizenWord);
            int points = 0;

            round.GuessWord = answer;
            round.GuessUsedAt = DateTime.UtcNow;
            round.GuessCorrect = correct;

            if (correct)
            {
                round.ResolvedAt = DateTime.UtcNow;
                round.ResolvedBy = "guess";
                round.Winner = "spy";
                points = config.PointsSpy;
            }

            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "SpyFall guess: session={SessionId}, spy={PlayerId}, correct={Correct}",
                sessionId, playerId, correct);

            if (correct)
            {
                if (points > 0)
                {
                    await _pointsAward.AwardAsync(
                        me.UserId,
                        points,
                        $"Шпионы: угадал слово (слово горожан: {round.CitizenWord})",
                        WalletTransactionType.EventReward,
                        session.Id,
                        ct);
                }

                await BroadcastLiveAsync(sessionId, ct);

                return new SpyFallOutcome(
                    true,
                    "🎯 Ты угадал слово! Победа шпиона",
                    await GetPlayerStateAsync(sessionId, playerId, ct),
                    points);
            }

            // Неверная попытка не завершает игру: шпион ждёт голосования.
            // Никому не рассылаем — горожане не должны знать, что попытка уже была.
            return new SpyFallOutcome(
                true,
                "❌ Неверное слово. Попытка использована — ждём голосования",
                await GetPlayerStateAsync(sessionId, playerId, ct));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Участник ли игрок в этой игре (для скрытия карточки от чужих).</summary>
    public async Task<bool> IsParticipantAsync(Guid sessionId, Guid playerId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.SpyFallParticipants.AsNoTracking()
            .AnyAsync(p => p.SessionId == sessionId && p.UserId == playerId, ct);
    }

    /// <summary>
    /// Ивент закрыли, а раунд не завершён: слова и шпион раскрываются, баллы никому.
    /// </summary>
    public async Task CloseUnresolvedAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            SpyFallRound? round = await db.SpyFallRounds
                .SingleOrDefaultAsync(r => r.SessionId == sessionId, ct);

            if (round is null || round.ResolvedAt is not null)
                return;

            round.ResolvedAt = DateTime.UtcNow;
            round.ResolvedBy = "closed";
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("SpyFall closed without result: session={SessionId}", sessionId);

            await BroadcastLiveAsync(sessionId, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Последняя игра (активная или недавно завершённая) для админ-вкладки.</summary>
    public async Task<object?> GetCurrentAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EventSession? session = await db.EventSessions
            .Include(s => s.Definition)
            .Where(s => s.Definition.Type == EventType && s.State == EventSessionState.Active)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        session ??= await db.EventSessions
            .Include(s => s.Definition)
            .Where(s => s.Definition.Type == EventType)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        return session is null ? null : await GetAdminStateAsync(session.Id, ct);
    }

    /// <summary>Полное состояние раунда для админ-вкладки: роли, слова, голоса, итог.</summary>
    public async Task<object?> GetAdminStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EventSession? session = await db.EventSessions
            .Include(s => s.Definition)
            .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null || session.Definition.Type != EventType)
            return null;

        SpyFallRound? round = await db.SpyFallRounds.AsNoTracking()
            .SingleOrDefaultAsync(r => r.SessionId == sessionId, ct);

        if (round is null)
            return null;

        List<SpyFallParticipant> participants = await db.SpyFallParticipants.AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .ToListAsync(ct);

        Dictionary<Guid, string> names = await GetNamesAsync(db, participants.Select(p => p.UserId), ct);

        return new
        {
            sessionId = session.Id,
            definitionId = session.DefinitionId,
            displayName = session.Definition.DisplayName,
            state = session.State.ToString(),
            startedAt = session.StartedAt,
            endsAt = session.EndsAt,
            phase = round.ResolvedAt is null ? "playing" : "finished",
            spyName = names.GetValueOrDefault(round.SpyUserId),
            citizenWord = round.CitizenWord,
            spyWord = round.SpyWord,
            guessUsed = round.GuessUsedAt is not null,
            guessWord = round.GuessWord,
            guessCorrect = round.GuessCorrect,
            winner = round.Winner,
            resolvedBy = round.ResolvedBy,
            votedCount = participants.Count(p => !p.IsSpy && p.VotedForId is not null),
            citizensCount = participants.Count(p => !p.IsSpy),
            participants = participants
                .OrderByDescending(p => p.IsSpy)
                .Select(p => new
                {
                    userId = p.UserId,
                    displayName = names.GetValueOrDefault(p.UserId),
                    isSpy = p.IsSpy,
                    hasVoted = p.VotedForId is not null,
                    votedForName = p.VotedForId.HasValue ? names.GetValueOrDefault(p.VotedForId.Value) : null
                })
                .ToList()
        };
    }

    /// <summary>Личные данные игрока: роль, слово, голос, попытка и итог.</summary>
    public async Task<object?> GetPlayerStateAsync(Guid sessionId, Guid playerId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EventSession? session = await db.EventSessions
            .Include(s => s.Definition)
            .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null || session.Definition.Type != EventType)
            return null;

        SpyFallRound? round = await db.SpyFallRounds.AsNoTracking()
            .SingleOrDefaultAsync(r => r.SessionId == sessionId, ct);

        if (round is null)
            return null;

        List<SpyFallParticipant> participants = await db.SpyFallParticipants.AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .ToListAsync(ct);

        SpyFallParticipant? me = participants.FirstOrDefault(p => p.UserId == playerId);
        if (me is null)
            return new { participates = false };

        SpyFallConfig config = ParseConfig(session.Definition.ConfigJson);
        Dictionary<Guid, string> names = await GetNamesAsync(db, participants.Select(p => p.UserId), ct);

        int citizensCount = participants.Count(p => !p.IsSpy);
        int votedCount = participants.Count(p => !p.IsSpy && p.VotedForId is not null);

        object? result = null;
        if (round.ResolvedAt is not null)
        {
            int myPoints = 0;
            if (round.Winner == "citizens" && !me.IsSpy && me.VotedForId == round.SpyUserId)
                myPoints = config.PointsCitizen;
            else if (round.Winner == "spy" && me.IsSpy)
                myPoints = config.PointsSpy;

            result = new
            {
                winner = round.Winner,
                resolvedBy = round.ResolvedBy,
                spyName = names.GetValueOrDefault(round.SpyUserId),
                citizenWord = round.CitizenWord,
                spyWord = round.SpyWord,
                guessWord = round.GuessWord,
                guessCorrect = round.GuessCorrect,
                myPoints,
                votes = participants
                    .Where(p => !p.IsSpy)
                    .Select(p => new
                    {
                        voterName = names.GetValueOrDefault(p.UserId),
                        targetName = p.VotedForId.HasValue ? names.GetValueOrDefault(p.VotedForId.Value) : null
                    })
                    .ToList()
            };
        }

        return new
        {
            participates = true,
            role = me.IsSpy ? "spy" : "citizen",
            isSpy = me.IsSpy,
            word = me.IsSpy ? round.SpyWord : round.CitizenWord,
            myVote = me.VotedForId,
            votedCount,
            citizensCount,
            attemptUsed = round.GuessUsedAt is not null,
            canVoteFor = participants
                .Where(p => p.UserId != playerId)
                .Select(p => new
                {
                    userId = p.UserId,
                    displayName = names.GetValueOrDefault(p.UserId)
                })
                .ToList(),
            result
        };
    }

    /// <summary>
    /// «Живые» данные для большого экрана: во время игры только счётчик голосов
    /// (кто проголосовал — секрет), после — раскрытие ролей, слов и голосов.
    /// </summary>
    public async Task<object?> GetLiveStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        EventSession? session = await db.EventSessions
            .Include(s => s.Definition)
            .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null || session.Definition.Type != EventType)
            return null;

        SpyFallRound? round = await db.SpyFallRounds.AsNoTracking()
            .SingleOrDefaultAsync(r => r.SessionId == sessionId, ct);

        if (round is null)
            return null;

        List<SpyFallParticipant> participants = await db.SpyFallParticipants.AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .ToListAsync(ct);

        int citizensCount = participants.Count(p => !p.IsSpy);
        int votedCount = participants.Count(p => !p.IsSpy && p.VotedForId is not null);

        if (round.ResolvedAt is null)
        {
            return new
            {
                phase = "playing",
                votedCount,
                citizensCount,
                playersCount = participants.Count
            };
        }

        Dictionary<Guid, string> names = await GetNamesAsync(db, participants.Select(p => p.UserId), ct);

        return new
        {
            phase = "finished",
            votedCount,
            citizensCount,
            playersCount = participants.Count,
            winner = round.Winner,
            resolvedBy = round.ResolvedBy,
            spyName = names.GetValueOrDefault(round.SpyUserId),
            citizenWord = round.CitizenWord,
            spyWord = round.SpyWord,
            guessWord = round.GuessWord,
            guessCorrect = round.GuessCorrect,
            votes = participants
                .Where(p => !p.IsSpy)
                .Select(p => new
                {
                    voterName = names.GetValueOrDefault(p.UserId),
                    targetName = p.VotedForId.HasValue ? names.GetValueOrDefault(p.VotedForId.Value) : null
                })
                .ToList()
        };
    }

    /// <summary>
    /// Итог голосования и начисление баллов: горожане побеждают, если у шпиона
    /// строго больше голосов, чем у любого другого; баллы — только их верным голосам.
    /// </summary>
    private async Task ResolveByVotesAsync(
        AppDbContext db,
        EventSession session,
        SpyFallRound round,
        List<SpyFallParticipant> participants,
        CancellationToken ct)
    {
        SpyFallConfig config = ParseConfig(session.Definition.ConfigJson);

        int spyVotes = participants.Count(p => p.VotedForId == round.SpyUserId);
        int maxOther = participants
            .Where(p => p.VotedForId is not null)
            .GroupBy(p => p.VotedForId!.Value)
            .Where(g => g.Key != round.SpyUserId)
            .Select(g => g.Count())
            .DefaultIfEmpty(0)
            .Max();

        bool citizensWon = spyVotes > maxOther;

        round.ResolvedAt = DateTime.UtcNow;
        round.ResolvedBy = "votes";
        round.Winner = citizensWon ? "citizens" : "spy";
        await db.SaveChangesAsync(ct);

        if (citizensWon)
        {
            if (config.PointsCitizen > 0)
            {
                foreach (SpyFallParticipant winner in participants.Where(p => !p.IsSpy && p.VotedForId == round.SpyUserId))
                {
                    await _pointsAward.AwardAsync(
                        winner.UserId,
                        config.PointsCitizen,
                        $"Шпионы: верный голос (слово: {round.CitizenWord})",
                        WalletTransactionType.EventReward,
                        session.Id,
                        ct);
                }
            }
        }
        else if (config.PointsSpy > 0)
        {
            await _pointsAward.AwardAsync(
                round.SpyUserId,
                config.PointsSpy,
                $"Шпионы: победа (слово горожан: {round.CitizenWord})",
                WalletTransactionType.EventReward,
                session.Id,
                ct);
        }

        _logger.LogInformation(
            "SpyFall resolved by votes: session={SessionId}, winner={Winner}, spyVotes={SpyVotes}, maxOther={MaxOther}",
            session.Id, round.Winner, spyVotes, maxOther);

        await BroadcastLiveAsync(session.Id, ct);
    }

    /// <summary>Рассылает свежие данные ивента экрану (EventLiveUpdated) и карточкам (SpyFallUpdated).</summary>
    private async Task BroadcastLiveAsync(Guid sessionId, CancellationToken ct)
    {
        object? live = await GetLiveStateAsync(sessionId, ct);
        if (live is null)
            return;

        await _hub.Clients.All.SendAsync("EventLiveUpdated", new { sessionId, live }, ct);
        await _hub.Clients.All.SendAsync("SpyFallUpdated", new { sessionId, live }, ct);
    }

    private static async Task<Dictionary<Guid, string>> GetNamesAsync(
        AppDbContext db,
        IEnumerable<Guid> userIds,
        CancellationToken ct)
    {
        List<Guid> ids = userIds.Distinct().ToList();

        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <summary>Сравнение ответа шпиона: регистр, лишние пробелы и ё/е не важны.</summary>
    public static string Normalize(string value)
    {
        string lowered = value.Trim().ToLowerInvariant().Replace('ё', 'е');
        return string.Join(' ', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Разбор конфига; при неверном JSON — значения по умолчанию.</summary>
    public static SpyFallConfig ParseConfig(string configJson)
    {
        SpyFallConfigDto? config = null;

        try
        {
            config = JsonSerializer.Deserialize<SpyFallConfigDto>(configJson, EventJsonOptions.Default);
        }
        catch (JsonException)
        {
            // Некорректный конфиг — играем с настройками по умолчанию
        }

        int pointsSpy = config?.PointsSpy is >= 0 ? config.PointsSpy : DefaultPointsSpy;
        int pointsCitizen = config?.PointsCitizen is >= 0 ? config.PointsCitizen : DefaultPointsCitizen;

        List<SpyFallPair> pairs = (config?.Pairs ?? new List<SpyFallPairDto>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Citizen) && !string.IsNullOrWhiteSpace(p.Spy))
            .Select(p => new SpyFallPair(p.Citizen.Trim(), p.Spy.Trim()))
            .ToList();

        return new SpyFallConfig(pointsSpy, pointsCitizen, pairs);
    }

    private sealed class SpyFallConfigDto
    {
        public int PointsSpy { get; set; } = DefaultPointsSpy;
        public int PointsCitizen { get; set; } = DefaultPointsCitizen;
        public List<SpyFallPairDto> Pairs { get; set; } = new();
    }

    private sealed class SpyFallPairDto
    {
        public string Citizen { get; set; } = string.Empty;
        public string Spy { get; set; } = string.Empty;
    }
}