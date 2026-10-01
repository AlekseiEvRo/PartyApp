using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

/// <summary>Фотография с вечеринки. Файл лежит на диске, в БД — только метаданные.</summary>
public class PartyPhoto: BaseEntity
{
    public Guid UploadedById { get; set; }
    public User UploadedBy { get; set; } = null!;

    public string StoragePath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public Guid? SessionId { get; set; }
    public EventSession? Session { get; set; }

    public ModerationStatus Status { get; set; } = ModerationStatus.Pending;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PhotoLike> Likes { get; set; } = new List<PhotoLike>();
}