using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Push;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Wallet;

/// <summary>
/// Единая точка изменения баланса: кошелёк, транзакция, новый баланс в SignalR и push.
/// </summary>
public class PointsAwardService : IPointsAwardService
{
    private static readonly TimeSpan BalancePushThrottle = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<PartyHub> _hubContext;
    private readonly IPushNotificationService _push;
    private readonly ILogger<PointsAwardService> _logger;

    public PointsAwardService(
        IServiceScopeFactory scopeFactory,
        IHubContext<PartyHub> hubContext,
        IPushNotificationService push,
        ILogger<PointsAwardService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _push = push;
        _logger = logger;
    }

    /// <summary>
    /// Начисляет баллы игроку: обновляет кошелёк, пишет транзакцию,
    /// шлёт новый баланс по SignalR и push-уведомление.
    /// </summary>
    public async Task<int> AwardAsync(
        Guid userId,
        int amount,
        string description,
        WalletTransactionType type = WalletTransactionType.EventReward,
        Guid? sessionId = null,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Domain.Entities.Wallet? wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == userId, ct);

        if (wallet is null)
        {
            wallet = new Domain.Entities.Wallet { UserId = userId, Balance = amount };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            wallet.Balance += amount;
            await db.SaveChangesAsync(ct);
        }

        db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = wallet.Id,
            Amount = amount,
            Type = type,
            Description = description,
            RelatedSessionId = sessionId
        });
        await db.SaveChangesAsync(ct);

        await NotifyBalanceChangedAsync(userId, wallet.Balance, amount, description, ct);

        return wallet.Balance;
    }

    /// <summary>
    /// Атомарно списывает баллы условным UPDATE: уйти в минус при параллельных
    /// покупках невозможно.
    /// </summary>
    public async Task<int?> TrySpendAsync(
        Guid userId,
        int amount,
        string description,
        WalletTransactionType type = WalletTransactionType.ShopPurchase,
        Guid? sessionId = null,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Списываемая сумма должна быть больше 0");

        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Domain.Entities.Wallet? wallet = await db.Wallets.AsNoTracking()
            .SingleOrDefaultAsync(w => w.UserId == userId, ct);

        if (wallet is null)
            return null;

        int updated = await db.Wallets
            .Where(w => w.Id == wallet.Id && w.Balance >= amount)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.Balance, w => w.Balance - amount), ct);

        if (updated == 0)
            return null;

        db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = wallet.Id,
            Amount = -amount,
            Type = type,
            Description = description,
            RelatedSessionId = sessionId
        });
        await db.SaveChangesAsync(ct);

        int newBalance = await db.Wallets.AsNoTracking()
            .Where(w => w.Id == wallet.Id)
            .Select(w => w.Balance)
            .SingleAsync(ct);

        await NotifyBalanceChangedAsync(userId, newBalance, -amount, description, ct);

        return newBalance;
    }

    /// <summary>
    /// Сообщает клиенту и push-ом, что баланс изменился.
    /// Вызывается отдельно, если кошелёк сохранён в общем DbContext вызывающего кода.
    /// </summary>
    public async Task NotifyBalanceChangedAsync(
        Guid userId,
        int newBalance,
        int amount,
        string description,
        CancellationToken ct = default)
    {
        await _hubContext.Clients.User(userId.ToString()).SendAsync("BalanceUpdated", new
        {
            balance = newBalance
        }, ct);

        if (amount == 0)
            return;

        await _push.SendToUserAsync(
            userId,
            new PushMessage(
                Title: amount > 0 ? $"⭐ +{amount} баллов" : $"−{-amount} баллов",
                Body: description,
                Url: "/",
                Tag: $"balance-{userId}",
                ThrottleWindow: BalancePushThrottle),
            ct);

        _logger.LogInformation(
            "Balance changed: user={UserId}, amount={Amount}, balance={Balance}, reason={Reason}",
            userId,
            amount,
            newBalance,
            description);
    }
}