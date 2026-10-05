using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

/// <summary>Отзыв о площадке, оставленный игроком на стенке отзывов.</summary>
public class Wish: BaseEntity
{
    public Guid PlayerId { get; set; }
    public User Player { get; set; } = null!;

    public string Text { get; set; } = string.Empty;
    public ModerationStatus Status { get; set; } = ModerationStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}