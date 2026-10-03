using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Пункт сценария вечеринки: админ собирает очередь ивентов заранее
/// и запускает их по порядку кнопкой «Запустить».
/// </summary>
public class PartyScheduleItem : BaseEntity
{
    public Guid PartyId { get; set; }
    public Party Party { get; set; } = null!;

    public Guid DefinitionId { get; set; }
    public EventDefinition Definition { get; set; } = null!;

    public int Order { get; set; }

    /// <summary>Когда пункт запустили. Пусто — ещё в очереди.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Сессия ивента, созданная при запуске пункта.</summary>
    public Guid? SessionId { get; set; }
}
