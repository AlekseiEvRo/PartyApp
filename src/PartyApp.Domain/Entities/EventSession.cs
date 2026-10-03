using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

public class EventSession: BaseEntity
{
    public Guid DefinitionId { get; set; }
    public EventDefinition Definition { get; set; } = null!;

    public Guid StartedById { get; set; }
    public User StartedBy { get; set; } = null!;

    public EventSessionState State { get; set; } = EventSessionState.Waiting;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Когда сессия завершится автоматически. Пусто — только вручную.</summary>
    public DateTime? EndsAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public ICollection<PlayerSubmission> Submissions { get; set; } = new List<PlayerSubmission>();
}