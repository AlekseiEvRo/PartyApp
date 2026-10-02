using PartyApp.Domain.Enums;

namespace PartyApp.Api.Modules.Wallet;

/// <summary>
/// Единая точка изменения баланса: кошелёк, транзакция, новый баланс в SignalR и push.
/// </summary>
public interface IPointsAwardService
{
    /// <summary>
    /// Начисляет баллы игроку: обновляет кошелёк, пишет транзакцию,
    /// шлёт новый баланс по SignalR и push-уведомление.
    /// </summary>
    Task<int> AwardAsync(
        Guid userId,
        int amount,
        string description,
        WalletTransactionType type = WalletTransactionType.EventReward,
        Guid? sessionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Атомарно списывает баллы, только если их хватает. Возвращает новый баланс
    /// или null, если кошелька нет или на счету меньше нужной суммы. Защищает
    /// от гонки при одновременных покупках.
    /// </summary>
    Task<int?> TrySpendAsync(
        Guid userId,
        int amount,
        string description,
        WalletTransactionType type = WalletTransactionType.ShopPurchase,
        Guid? sessionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Переводит баллы от одного игрока другому: одной транзакцией БД атомарно
    /// списывает у отправителя (не уходя в минус) и начисляет получателю, пишет
    /// две транзакции TransferOut/TransferIn. Возвращает новые балансы обоих
    /// или null, если кошелька/баллов у отправителя не хватает.
    /// </summary>
    Task<TransferOutcome?> TransferAsync(
        Guid fromUserId,
        Guid toUserId,
        int amount,
        string? comment = null,
        CancellationToken ct = default);

    /// <summary>
    /// Сообщает клиенту и push-ом, что баланс изменился.
    /// Вызывается отдельно, если кошелёк сохранён в общем DbContext вызывающего кода.
    /// </summary>
    Task NotifyBalanceChangedAsync(
        Guid userId,
        int newBalance,
        int amount,
        string description,
        CancellationToken ct = default);
}

/// <summary>Новые балансы отправителя и получателя после перевода.</summary>
public record TransferOutcome(int SenderBalance, int RecipientBalance);