using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Участник раунда «Шпионы»: роль, слово и голос против подозреваемого.
/// Голос горожанина можно менять, пока не проголосовали все.
/// </summary>
public class SpyFallParticipant: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public bool IsSpy { get; set; }

    /// <summary>За кого проголосовал горожанин; null — ещё не голосовал.</summary>
    public Guid? VotedForId { get; set; }

    public DateTime? VotedAt { get; set; }
}