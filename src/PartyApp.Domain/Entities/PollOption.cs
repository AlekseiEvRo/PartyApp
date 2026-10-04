using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>Вариант голосования — ссылка на определение ивента.</summary>
public class PollOption : BaseEntity
{
    public Guid PollId { get; set; }
    public Poll Poll { get; set; } = null!;

    public Guid DefinitionId { get; set; }
    public EventDefinition Definition { get; set; } = null!;

    public int Order { get; set; }
}
