using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Hubs;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Services;

public record RaffleDrawOutcome(bool Success, string Message, object? Data = null);

/// <summary>
/// Лототрон: игроки получают билеты со случайными номерами (заявки — обычные сабмиты),
/// админ запускает розыгрыш, сервер выбирает случайный билет и рассылает только
/// победный номер — имена игроков на экран не попадают.
/// </summary>
public class RaffleService
{
    /// <summary>Максимальный номер билета: номера выдаются в диапазоне 1..9999.</summary>
    public const int MaxTicketNumber = 9999;

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

            List<PlayerSubmission> submissions = await db.PlayerSubmissions
                .Where(s => s.SessionId == sessionId)
                .OrderBy(s => s.SubmittedAt)
                .ToListAsync(ct);

            if (submissions.Count == 0)
                return new RaffleDrawOutcome(false, "Пока никто не участвует");

            // Заявки старого формата (до появления билетов) получают номера при первом
            // розыгрыше. Если в сессии уже есть билеты, заявки без номера — это отклонённые
            // попытки («Ты уже участвуешь», не хватило баллов и т.п.), они не участвуют.
            if (submissions.All(s => s.TicketNumber is null))
            {
                EnsureTicketNumbers(submissions);
                await db.SaveChangesAsync(ct);
            }

            List<PlayerSubmission> tickets = submissions
                .Where(s => s.TicketNumber.HasValue)
                .ToList();

            if (tickets.Count == 0)
                return new RaffleDrawOutcome(false, "Пока никто не участвует");

            PlayerSubmission winner = tickets[Random.Shared.Next(tickets.Count)];
            int winnerTicket = winner.TicketNumber!.Value;

            db.RaffleDraws.Add(new RaffleDraw
            {
                SessionId = sessionId,
                WinnerId = winner.PlayerId,
                WinnerTicketNumber = winnerTicket
            });
            await db.SaveChangesAsync(ct);

            string winnerName = await db.Users
                .Where(u => u.Id == winner.PlayerId)
                .Select(u => u.DisplayName)
                .SingleAsync(ct);

            object payload = new
            {
                sessionId,
                winnerTicket,
                ticketsCount = tickets.Count
            };

            await _hub.Clients.All.SendAsync("RaffleDrawn", payload, ct);

            _logger.LogInformation(
                "Raffle drawn: session={SessionId}, winner={Winner}, ticket={Ticket}, tickets={Tickets}",
                sessionId, winnerName, winnerTicket, tickets.Count);

            return new RaffleDrawOutcome(true, $"🏆 Выиграл билет №{winnerTicket} ({winnerName})", new
            {
                winnerTicket,
                winnerName,
                ticketsCount = tickets.Count
            });
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

        List<int> tickets = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.TicketNumber != null)
            .Select(s => s.TicketNumber!.Value)
            .OrderBy(n => n)
            .ToListAsync(ct);

        int playersCount = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId)
            .Select(s => s.PlayerId)
            .Distinct()
            .CountAsync(ct);

        RaffleDraw? draw = await db.RaffleDraws
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.SessionId == sessionId, ct);

        return new
        {
            tickets,
            ticketsCount = tickets.Count,
            playersCount,
            winnerTicket = draw?.WinnerTicketNumber
        };
    }

    /// <summary>Свободный случайный номер билета в диапазоне 1..9999.</summary>
    public static int? PickFreeTicketNumber(IEnumerable<int> existingNumbers)
    {
        var used = new HashSet<int>(existingNumbers);

        if (used.Count >= MaxTicketNumber)
            return null;

        // Сначала пробуем случайно; если диапазон плотно занят — добираем перебором
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int candidate = Random.Shared.Next(1, MaxTicketNumber + 1);
            if (!used.Contains(candidate))
                return candidate;
        }

        for (int candidate = 1; candidate <= MaxTicketNumber; candidate++)
        {
            if (!used.Contains(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Выдаёт номера заявкам без них (сессии старого формата).</summary>
    private static void EnsureTicketNumbers(List<PlayerSubmission> submissions)
    {
        var used = new HashSet<int>();
        foreach (PlayerSubmission submission in submissions)
        {
            if (submission.TicketNumber.HasValue)
                used.Add(submission.TicketNumber.Value);
        }

        foreach (PlayerSubmission submission in submissions)
        {
            if (submission.TicketNumber.HasValue)
                continue;

            int? number = PickFreeTicketNumber(used);
            if (number is null)
                break;

            submission.TicketNumber = number.Value;
            used.Add(number.Value);
        }
    }
}
