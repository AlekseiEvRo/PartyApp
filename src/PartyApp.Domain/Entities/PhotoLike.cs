using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>Лайк фотографии. У одного игрока — не больше одного лайка на фото.</summary>
public class PhotoLike: BaseEntity
{
    public Guid PhotoId { get; set; }
    public PartyPhoto Photo { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}