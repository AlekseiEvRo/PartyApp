using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "raffle" (лототрон). Игрок жмёт «Участвовать» и получает билет
/// со случайным номером; первый билет бесплатный, дополнительные — по цене из конфига.
/// Победивший билет выбирает сервер по кнопке админа (см. <see cref="RaffleService"/>).
/// </summary>
public class RaffleHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RaffleService _raffle;
    private readonly IPointsAwardService _pointsAward;
    private readonly ILogger<RaffleHandler> _logger;

    public RaffleHandler(
        IServiceScopeFactory scopeFactory,
        RaffleService raffle,
        IPointsAwardService pointsAward,
        ILogger<RaffleHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _raffle = raffle;
        _pointsAward = pointsAward;
        _logger = logger;
    }

    public string EventType => "raffle";

    public string DefaultConfigJson => """{"prize":"Приз","ticketPrice":0,"maxTickets":1}""";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        RaffleConfig config = ParseConfig(definition.ConfigJson);

        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<int?> existingNumbers = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id)
            .Select(s => s.TicketNumber)
            .ToListAsync(ct);

        List<int> myTicketNumbers = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id
                        && s.PlayerId == playerId
                        && s.TicketNumber != null)
            .Select(s => s.TicketNumber!.Value)
            .OrderBy(n => n)
            .ToListAsync(ct);

        int myTickets = myTicketNumbers.Count;

        if (myTickets >= config.MaxTickets)
        {
            return SubmissionResult.Fail(config.MaxTickets == 1
                ? "Ты уже участвуешь"
                : $"Лимит билетов исчерпан ({config.MaxTickets})");
        }

        int? ticketNumber = RaffleService.PickFreeTicketNumber(
            existingNumbers.Where(n => n.HasValue).Select(n => n!.Value));

        if (ticketNumber is null)
            return SubmissionResult.Fail("Свободные номера билетов закончились");

        // Первый билет бесплатный, за каждый следующий списываем баллы
        int cost = myTickets == 0 ? 0 : config.TicketPrice;
        if (cost > 0)
        {
            int? balance = await _pointsAward.TrySpendAsync(
                playerId,
                cost,
                "Билет лототрона",
                WalletTransactionType.RaffleTicket,
                session.Id,
                ct);

            if (balance is null)
                return SubmissionResult.Fail($"Не хватает баллов: билет стоит {cost}");
        }

        int playersCount = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id)
            .Select(s => s.PlayerId)
            .Distinct()
            .CountAsync(ct);

        bool firstTicket = myTickets == 0;
        int totalTickets = myTickets + 1;
        int[] myTicketsAfter = myTicketNumbers.Append(ticketNumber.Value).OrderBy(n => n).ToArray();

        // Если игрок уже писал в сессию (например, отклонённая попытка), в счётчике он уже есть
        bool alreadyCounted = !firstTicket || await db.PlayerSubmissions
            .AnyAsync(s => s.SessionId == session.Id && s.PlayerId == playerId, ct);

        _logger.LogInformation(
            "Raffle: player {PlayerId} got ticket {TicketNumber} in session {SessionId} (cost={Cost})",
            playerId, ticketNumber, session.Id, cost);

        return SubmissionResult.Ok(
            0,
            $"Билет №{ticketNumber} твой! 🎟",
            new
            {
                joined = true,
                ticketNumber,
                myTickets = myTicketsAfter,
                maxTickets = config.MaxTickets,
                nextTicketPrice = NextTicketPrice(config, totalTickets),
                playersCount = alreadyCounted ? playersCount : playersCount + 1,
                ticketsCount = existingNumbers.Count(n => n.HasValue) + 1
            },
            ticketNumber: ticketNumber);
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
        RaffleConfig config = ParseConfig(definition.ConfigJson);

        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<int> myTickets = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id && s.PlayerId == playerId && s.TicketNumber != null)
            .Select(s => s.TicketNumber!.Value)
            .OrderBy(n => n)
            .ToListAsync(ct);

        int playersCount = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id)
            .Select(s => s.PlayerId)
            .Distinct()
            .CountAsync(ct);

        return new
        {
            joined = myTickets.Count > 0,
            myTickets,
            maxTickets = config.MaxTickets,
            nextTicketPrice = NextTicketPrice(config, myTickets.Count),
            playersCount
        };
    }

    /// <summary>
    /// Цена следующего билета: первый бесплатный, дальше — ticketPrice;
    /// 0 — следующий билет бесплатен или лимит уже достигнут.
    /// </summary>
    private static int NextTicketPrice(RaffleConfig config, int ownedTickets)
    {
        if (ownedTickets >= config.MaxTickets)
            return 0;

        return ownedTickets == 0 ? 0 : config.TicketPrice;
    }

    private static RaffleConfig ParseConfig(string configJson)
    {
        RaffleConfig? config = null;

        try
        {
            config = JsonSerializer.Deserialize<RaffleConfig>(configJson, EventJsonOptions.Default);
        }
        catch (JsonException)
        {
            // Некорректный конфиг — работаем со значениями по умолчанию
        }

        return new RaffleConfig
        {
            TicketPrice = config?.TicketPrice > 0 ? config.TicketPrice : 0,
            MaxTickets = config?.MaxTickets > 0 ? config.MaxTickets : 1
        };
    }

    private class RaffleConfig
    {
        public int TicketPrice { get; set; }
        public int MaxTickets { get; set; } = 1;
    }
}
