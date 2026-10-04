using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>Голос игрока: один на голосование, до закрытия можно передумать.</summary>
public class PollVote : BaseEntity
{
    public Guid PollId { get; set; }
    public Poll Poll { get; set; } = null!;

    public Guid OptionId { get; set; }
    public PollOption Option { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
