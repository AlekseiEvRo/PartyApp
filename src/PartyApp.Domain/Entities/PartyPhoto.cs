using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

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

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}