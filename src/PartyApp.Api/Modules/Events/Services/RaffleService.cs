using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Services;

public record RaffleDrawOutcome(bool Success, string Message, object? Data = null);

/// <summary>
/// Лототрон: игроки жмут «Участвовать» (заявки — обычные сабмиты),
/// админ запускает розыгрыш, сервер выбирает одного победителя
/// и рассылает участников с победителем для анимации колеса.
/// </summary>
public class RaffleService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<PartyHub> _hub;
    private readonly ILogger<RaffleService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public RaffleService(
        IServiceScopeFactory scopeFactory,
        IHubContext<PartyHub> hub,
        ILogger<RaffleService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    public async Task<RaffleDrawOutcome> DrawAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            EventSession? session = await db.EventSessions
                .Include(s => s.Definition)
                .SingleOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null || session.Definition.Type != "raffle")
                return new RaffleDrawOutcome(false, "Сессия лототрона не найдена");

            bool alreadyDrawn = await db.RaffleDraws.AnyAsync(d => d.SessionId == sessionId, ct);
            if (alreadyDrawn)
                return new RaffleDrawOutcome(false, "Розыгрыш уже проводился");

            List<RaffleParticipant> participants = await GetParticipantsAsync(db, sessionId, ct);
            if (participants.Count == 0)
                return new RaffleDrawOutcome(false, "Пока никто не участвует");

            RaffleParticipant winner = participants[Random.Shared.Next(participants.Count)];

            db.RaffleDraws.Add(new RaffleDraw
            {
                SessionId = sessionId,
                WinnerId = winner.Id
            });
            await db.SaveChangesAsync(ct);

            object payload = new
            {
                sessionId,
                winner = new { id = winner.Id, name = winner.Name },
                participants = participants.Select(p => new { p.Id, p.Name }).ToArray()
            };

            await _hub.Clients.All.SendAsync("RaffleDrawn", payload, ct);

            _logger.LogInformation(
                "Raffle drawn: session={SessionId}, winner={Winner}, participants={Count}",
                sessionId, winner.Name, participants.Count);

            return new RaffleDrawOutcome(true, $"🏆 Победитель: {winner.Name}", payload);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<object?> GetStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<RaffleParticipant> participants = await GetParticipantsAsync(db, sessionId, ct);

        RaffleDraw? draw = await db.RaffleDraws
            .AsNoTracking()
            .Include(d => d.Winner)
            .SingleOrDefaultAsync(d => d.SessionId == sessionId, ct);

        return new
        {
            participants = participants.Select(p => new { p.Id, p.Name }).ToArray(),
            winner = draw is null ? null : new { id = draw.WinnerId, name = draw.Winner.DisplayName }
        };
    }

    private static async Task<List<RaffleParticipant>> GetParticipantsAsync(
        AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        var rows = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId)
            .OrderBy(s => s.SubmittedAt)
            .Select(s => new { s.PlayerId, Name = s.Player.DisplayName })
            .ToListAsync(ct);

        var seen = new HashSet<Guid>();
        var participants = new List<RaffleParticipant>();

        foreach (var row in rows)
        {
            if (seen.Add(row.PlayerId))
                participants.Add(new RaffleParticipant(row.PlayerId, row.Name));
        }

        return participants;
    }

    private record RaffleParticipant(Guid Id, string Name);
}