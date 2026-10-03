using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

/// <summary>Пожелание или тост, оставленный игроком на «стенке пожеланий».</summary>
public class Wish: BaseEntity
{
    /// <summary>Вечеринка, во время которой оставили пожелание. Пусто — партии ещё не было.</summary>
    public Guid? PartyId { get; set; }

    public Guid PlayerId { get; set; }
    public User Player { get; set; } = null!;

    public string Text { get; set; } = string.Empty;
    public ModerationStatus Status { get; set; } = ModerationStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}