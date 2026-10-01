using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Push;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Shop;

public record BidOutcome(bool Success, string Message, int? NewBalance = null);

public record LotCloseOutcome(bool Success, string Message, object? Data = null);

/// <summary>
/// Аукцион с закрытыми ставками. Ставка удерживает баллы: при повышении
/// списывается разница, при закрытии лота проигравшим всё возвращается,
/// победитель платит свою ставку. Операции сериализуются семафором —
/// приложение работает в одном процессе.
/// </summary>
public class AuctionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly IHubContext<PartyHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuctionService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public AuctionService(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        IHubContext<PartyHub> hub,
        IPushNotificationService push,
        TimeProvider timeProvider,
        ILogger<AuctionService> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _hub = hub;
        _push = push;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<BidOutcome> PlaceBidAsync(
        Guid lotId,
        Guid playerId,
        int amount,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Lot? lot = await db.Lots
                .Include(l => l.Bids)
                .SingleOrDefaultAsync(l => l.Id == lotId, ct);

            if (lot is null)
                return new BidOutcome(false, "Лот не найден");

            if (lot.Status != LotStatus.Open)
                return new BidOutcome(false, "Аукцион уже завершён");

            if (_timeProvider.GetUtcNow().UtcDateTime >= lot.EndsAt)
                return new BidOutcome(false, "Приём ставок закончился");

            if (amount < lot.MinBid)
                return new BidOutcome(false, $"Минимальная ставка — {lot.MinBid}");

            Bid? bid = lot.Bids.FirstOrDefault(b => b.PlayerId == playerId);
            int delta = amount - (bid?.Amount ?? 0);

            if (delta <= 0)
                return new BidOutcome(false, "Ставка должна быть больше вашей текущей");

            int? newBalance = await _pointsAward.TrySpendAsync(
                playerId,
                delta,
                $"Ставка на лот «{lot.Name}»",
                WalletTransactionType.AuctionBid,
                ct: ct);

            if (newBalance is null)
                return new BidOutcome(false, "Недостаточно баллов");

            if (bid is null)
            {
                db.Bids.Add(new Bid
                {
                    LotId = lotId,
                    PlayerId = playerId,
                    Amount = amount
                });
            }
            else
            {
                bid.Amount = amount;
                bid.CreatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            }

            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Bid placed: lot={LotId}, player={PlayerId}, amount={Amount}",
                lotId, playerId, amount);

            return new BidOutcome(true, "Ставка принята", newBalance);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Закрывает лот: побеждает максимальная ставка, остальным возврат.</summary>
    public async Task<LotCloseOutcome> CloseLotAsync(Guid lotId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Lot? lot = await db.Lots
                .Include(l => l.Bids).ThenInclude(b => b.Player)
                .SingleOrDefaultAsync(l => l.Id == lotId, ct);

            if (lot is null)
                return new LotCloseOutcome(false, "Лот не найден");

            if (lot.Status != LotStatus.Open)
                return new LotCloseOutcome(false, "Лот уже закрыт");

            Bid? winnerBid = lot.Bids
                .OrderByDescending(b => b.Amount)
                .ThenBy(b => b.CreatedAt)
                .FirstOrDefault();

            lot.Status = LotStatus.Finished;
            lot.WinnerId = winnerBid?.PlayerId;
            lot.WinningBid = winnerBid?.Amount;
            await db.SaveChangesAsync(ct);

            if (winnerBid is not null)
            {
                foreach (Bid bid in lot.Bids.Where(b => b.PlayerId != winnerBid.PlayerId))
                {
                    await _pointsAward.AwardAsync(
                        bid.PlayerId,
                        bid.Amount,
                        $"Возврат ставки по лоту «{lot.Name}»",
                        WalletTransactionType.Refund,
                        ct: ct);
                }
            }

            object summary = new
            {
                lotId = lot.Id,
                name = lot.Name,
                winnerName = winnerBid?.Player.DisplayName,
                winningBid = winnerBid?.Amount
            };

            await _hub.Clients.All.SendAsync("LotFinished", summary, ct);

            if (winnerBid is not null)
            {
                await _push.SendToAllAsync(
                    new PushMessage(
                        Title: "🏆 Аукцион завершён",
                        Body: $"{lot.Name}: победил {winnerBid.Player.DisplayName} ({winnerBid.Amount} баллов)",
                        Url: "/",
                        Tag: $"lot-{lot.Id}"),
                    ct: ct);
            }

            _logger.LogInformation(
                "Lot closed: {LotId}, winner={Winner}, amount={Amount}",
                lot.Id, winnerBid?.Player.DisplayName, winnerBid?.Amount);

            return new LotCloseOutcome(true, "Лот закрыт", summary);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Отменяет лот и возвращает все ставки.</summary>
    public async Task<LotCloseOutcome> CancelLotAsync(Guid lotId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Lot? lot = await db.Lots
                .Include(l => l.Bids)
                .SingleOrDefaultAsync(l => l.Id == lotId, ct);

            if (lot is null)
                return new LotCloseOutcome(false, "Лот не найден");

            if (lot.Status != LotStatus.Open)
                return new LotCloseOutcome(false, "Лот уже закрыт");

            lot.Status = LotStatus.Cancelled;
            await db.SaveChangesAsync(ct);

            foreach (Bid bid in lot.Bids)
            {
                await _pointsAward.AwardAsync(
                    bid.PlayerId,
                    bid.Amount,
                    $"Отмена лота «{lot.Name}»: возврат ставки",
                    WalletTransactionType.Refund,
                    ct: ct);
            }

            await _hub.Clients.All.SendAsync("LotCancelled", new { lotId = lot.Id, lot.Name }, ct);

            return new LotCloseOutcome(true, "Лот отменён, ставки возвращены");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Закрывает все лоты, у которых истёк срок.</summary>
    public async Task CloseExpiredAsync(CancellationToken ct = default)
    {
        List<Guid> ids;

        using (var scope = _scopeFactory.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            ids = await db.Lots.AsNoTracking()
                .Where(l => l.Status == LotStatus.Open && l.EndsAt <= now)
                .Select(l => l.Id)
                .ToListAsync(ct);
        }

        foreach (Guid id in ids)
        {
            await CloseLotAsync(id, ct);
        }
    }
}

/// <summary>Раз в 10 секунд закрывает лоты, у которых истёк срок приёма ставок.</summary>
public class AuctionClosingService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly AuctionService _auction;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuctionClosingService> _logger;

    public AuctionClosingService(
        AuctionService auction,
        TimeProvider timeProvider,
        ILogger<AuctionClosingService> logger)
    {
        _auction = auction;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _auction.CloseExpiredAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Не удалось закрыть просроченные лоты");
            }

            try
            {
                await Task.Delay(Interval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}