namespace PartyApp.Api.Modules.Push;

/// <summary>
/// Push-сообщение для отправки на устройство.
/// </summary>
/// <param name="Title">Заголовок уведомления.</param>
/// <param name="Body">Текст уведомления.</param>
/// <param name="Url">Куда вести при нажатии на уведомление.</param>
/// <param name="Tag">Тег: ОС заменяет уведомление с таким же тегом, а не копит их.</param>
/// <param name="ThrottleWindow">Минимальный интервал между отправками одному пользователю (для «шумных» событий).</param>
public record PushMessage(
    string Title,
    string? Body = null,
    string? Url = "/",
    string? Tag = null,
    TimeSpan? ThrottleWindow = null);

/// <summary>
/// Итог отправки push.
/// </summary>
/// <param name="Sent">Успешно доставлено.</param>
/// <param name="Failed">Ошибки отправки.</param>
/// <param name="Removed">Удалено устаревших подписок (404/410).</param>
public record PushDeliveryReport(int Sent, int Failed, int Removed);

public interface IPushNotificationService
{
    /// <summary>Публичный VAPID-ключ или null, если push не настроен.</summary>
    string? PublicKey { get; }

    /// <summary>Отправляет push всем подписчикам, не блокируя вызывающий код.</summary>
    Task SendToAllAsync(PushMessage message, Guid? excludedUserId = null, CancellationToken ct = default);

    /// <summary>Отправляет push всем устройствам пользователя, не блокируя вызывающий код.</summary>
    Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct = default);

    /// <summary>Отправляет push нескольким пользователям (например, участникам игры), не блокируя вызывающий код.</summary>
    Task SendToUsersAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, CancellationToken ct = default);

    /// <summary>Отправляет push пользователю и ждёт результат (для отладки и тестового уведомления).</summary>
    Task<PushDeliveryReport> SendToUserNowAsync(Guid userId, PushMessage message, CancellationToken ct = default);
}