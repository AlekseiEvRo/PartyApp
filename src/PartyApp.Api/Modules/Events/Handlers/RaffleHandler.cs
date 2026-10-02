using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "raffle" (лототрон). Игрок жмёт «Участвовать»,
/// победителя выбирает сервер по кнопке админа (см. <see cref="RaffleService"/>).
/// </summary>
public class RaffleHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RaffleService _raffle;
    private readonly ILogger<RaffleHandler> _logger;

    public RaffleHandler(
        IServiceScopeFactory scopeFactory,
        RaffleService raffle,
        ILogger<RaffleHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _raffle = raffle;
        _logger = logger;
    }

    public string EventType => "raffle";

    public string DefaultConfigJson => """{"prize":"Приз"}""";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        bool alreadyJoined = await db.PlayerSubmissions
            .AnyAsync(s => s.SessionId == session.Id && s.PlayerId == playerId, ct);

        if (alreadyJoined)
            return SubmissionResult.Fail("Ты уже участвуешь");

        int participants = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id)
            .Select(s => s.PlayerId)
            .Distinct()
            .CountAsync(ct);

        _logger.LogInformation(
            "Raffle: player {PlayerId} joined session {SessionId}",
            playerId, session.Id);

        return SubmissionResult.Ok(
            0,
            "Ты в игре! 🎟",
            new { joined = true, participants = participants + 1 });
    }

    public Task<object?> GetLiveDataAsync(EventSession session, EventDefinition definition, CancellationToken ct = default)
    {
        return _raffle.GetStateAsync(session.Id, ct);
    }

    public async Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        bool joined = await db.PlayerSubmissions
            .AnyAsync(s => s.SessionId == session.Id && s.PlayerId == playerId, ct);

        int participants = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id)
            .Select(s => s.PlayerId)
            .Distinct()
            .CountAsync(ct);

        return new { joined, participants };
    }
}