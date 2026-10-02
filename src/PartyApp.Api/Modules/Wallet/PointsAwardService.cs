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
    /// Переводит баллы между игроками одной транзакцией БД: списание условным
    /// UPDATE (уйти в минус нельзя), начисление получателю и две транзакции.
    /// </summary>
    public async Task<TransferOutcome?> TransferAsync(
        Guid fromUserId,
        Guid toUserId,
        int amount,
        string? comment = null,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Сумма перевода должна быть больше 0");

        if (fromUserId == toUserId)
            throw new ArgumentException("Нельзя перевести баллы самому себе", nameof(toUserId));

        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User? sender = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == fromUserId, ct);
        User? recipient = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == toUserId, ct);

        if (sender is null || recipient is null)
            return null;

        Domain.Entities.Wallet? senderWallet = await db.Wallets.AsNoTracking()
            .SingleOrDefaultAsync(w => w.UserId == fromUserId, ct);

        if (senderWallet is null)
            return null;

        Domain.Entities.Wallet? recipientWallet = await db.Wallets.AsNoTracking()
            .SingleOrDefaultAsync(w => w.UserId == toUserId, ct);

        string commentSuffix = string.IsNullOrWhiteSpace(comment) ? string.Empty : $": {comment.Trim()}";
        string senderDescription = TrimDescription($"Перевод игроку {recipient.DisplayName}{commentSuffix}");
        string recipientDescription = TrimDescription($"Перевод от {sender.DisplayName}{commentSuffix}");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        int spent = await db.Wallets
            .Where(w => w.Id == senderWallet.Id && w.Balance >= amount)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.Balance, w => w.Balance - amount), ct);

        if (spent == 0)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        if (recipientWallet is null)
        {
            // Кошелёк создаётся при регистрации, но поддержим и отсутствующий.
            recipientWallet = new Domain.Entities.Wallet { UserId = toUserId, Balance = amount };
            db.Wallets.Add(recipientWallet);
        }
        else
        {
            await db.Wallets
                .Where(w => w.Id == recipientWallet.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(w => w.Balance, w => w.Balance + amount), ct);
        }

        db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = senderWallet.Id,
            Amount = -amount,
            Type = WalletTransactionType.TransferOut,
            Description = senderDescription
        });

        db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = recipientWallet.Id,
            Amount = amount,
            Type = WalletTransactionType.TransferIn,
            Description = recipientDescription
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        int senderBalance = await db.Wallets.AsNoTracking()
            .Where(w => w.Id == senderWallet.Id)
            .Select(w => w.Balance)
            .SingleAsync(ct);

        int recipientBalance = await db.Wallets.AsNoTracking()
            .Where(w => w.Id == recipientWallet.Id)
            .Select(w => w.Balance)
            .SingleAsync(ct);

        await NotifyBalanceChangedAsync(fromUserId, senderBalance, -amount, senderDescription, ct);
        await NotifyBalanceChangedAsync(toUserId, recipientBalance, amount, recipientDescription, ct);

        _logger.LogInformation(
            "Transfer: from={FromUserId}, to={ToUserId}, amount={Amount}",
            fromUserId,
            toUserId,
            amount);

        return new TransferOutcome(senderBalance, recipientBalance);
    }

    private static string TrimDescription(string description)
    {
        const int maxLength = 500;
        return description.Length <= maxLength ? description : description[..maxLength];
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