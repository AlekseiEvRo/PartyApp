using PartyApp.Domain.Entities;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик логики ивента. Каждый тип ивента реализует этот интерфейс.
/// Автоматически регистрируется через reflection при старте приложения.
/// </summary>
public interface IEventHandler
{
    /// <summary>
    /// Уникальный ключ типа ивента: "quick_checkin", "quiz", "promo_code" и т.д.
    /// Должен совпадать с EventDefinition.Type.
    /// </summary>
    string EventType { get; }

    /// <summary>
    /// Обрабатывает действие игрока в рамках активного ивента.
    /// </summary>
    /// <param name="session">Активная сессия ивента</param>
    /// <param name="definition">Определение ивента с конфигом</param>
    /// <param name="playerId">ID игрока</param>
    /// <param name="payloadJson">Данные от игрока (JSON)</param>
    /// <param name="ct">CancellationToken</param>
    /// <returns>Результат обработки</returns>
    Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default);

    /// <summary>
    /// Вызывается при старте сессии. Можно использовать для инициализации состояния.
    /// </summary>
    Task OnSessionStartedAsync(EventSession session, EventDefinition definition, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Вызывается при завершении сессии. Можно использовать для подсчёта итогов.
    /// </summary>
    Task OnSessionFinishedAsync(EventSession session, EventDefinition definition, CancellationToken ct = default)
        => Task.CompletedTask;
}