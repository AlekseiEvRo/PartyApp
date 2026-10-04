using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Голосование за следующий ивент: админ выбирает варианты из определений,
/// игроки голосуют, победителя можно запустить одной кнопкой.
/// </summary>
public class Poll : BaseEntity
{
    public string Question { get; set; } = string.Empty;
    public PollStatus Status { get; set; } = PollStatus.Open;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }

    public ICollection<PollOption> Options { get; set; } = new List<PollOption>();
    public ICollection<PollVote> Votes { get; set; } = new List<PollVote>();
}
