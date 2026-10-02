using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "reaction" («Кто быстрее»).
/// Сервер задаёт момент старта: до сигнала нажимать нельзя, после — чем
/// быстрее игрок нажал, тем больше баллов. Время берётся серверное,
/// поэтому читерить бесполезно.
/// </summary>
public class ReactionHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReactionHandler> _logger;

    public ReactionHandler(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        TimeProvider timeProvider,
        ILogger<ReactionHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string EventType => "reaction";

    public string DefaultConfigJson => """{"delaySec":5,"timeLimitSec":15,"points":10}""";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<ReactionConfig>(definition.ConfigJson, EventJsonOptions.Default);
        int delaySec = Math.Max(0, config?.DelaySec ?? 5);
        int timeLimitSec = config?.TimeLimitSec > 0 ? config.TimeLimitSec : 15;
        int maxPoints = config?.Points > 0 ? config.Points : 10;

        DateTime startUtc = session.StartedAt.AddSeconds(delaySec);
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        if (nowUtc < startUtc)
        {
            return SubmissionResult.Fail(
                "Рано! Дождись сигнала",
                new { startsInMs = (int)(startUtc - nowUtc).TotalMilliseconds });
        }

        double elapsedMs = (nowUtc - startUtc).TotalMilliseconds;
        if (elapsedMs > timeLimitSec * 1000)
            return SubmissionResult.Fail("Слишком поздно — время вышло");

        if (await HasReactedAsync(session.Id, playerId, ct))
            return SubmissionResult.Fail("Ты уже нажимал");

        // Каждые 300 мс штрафуют на 1 балл, минимум 1 балл
        int penalty = (int)(elapsedMs / 300);
        int points = Math.Max(1, maxPoints - penalty);

        await _pointsAward.AwardAsync(
            playerId,
            points,
            "Реакция: кто быстрее",
            sessionId: session.Id,
            ct: ct);

        _logger.LogInformation(
            "Reaction: player {PlayerId} reacted in {ElapsedMs} ms (session {SessionId})",
            playerId, elapsedMs, session.Id);

        return SubmissionResult.Ok(
            points,
            $"⚡ {elapsedMs:0} мс!",
            new { elapsedMs = (int)elapsedMs, points });
    }

    public Task<object?> GetLiveDataAsync(EventSession session, EventDefinition definition, CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<ReactionConfig>(definition.ConfigJson, EventJsonOptions.Default);
        int delaySec = Math.Max(0, config?.DelaySec ?? 5);

        return Task.FromResult<object?>(new
        {
            startsAtUtc = session.StartedAt.AddSeconds(delaySec)
        });
    }

    public async Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        return new { reacted = await HasReactedAsync(session.Id, playerId, ct) };
    }

    private async Task<bool> HasReactedAsync(Guid sessionId, Guid playerId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.PlayerSubmissions
            .AnyAsync(s => s.SessionId == sessionId
                           && s.PlayerId == playerId
                           && s.Score > 0, ct);
    }

    private class ReactionConfig
    {
        public int DelaySec { get; set; } = 5;
        public int TimeLimitSec { get; set; } = 15;
        public int Points { get; set; } = 10;
    }
}