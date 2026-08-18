using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

public class QrToken: BaseEntity
{
    /// <summary>Уникальный код, зашитый в QR</summary>
    public string Code { get; set; } = string.Empty;

    public int Points { get; set; }

    public Guid? RedeemedById { get; set; }
    public User? RedeemedBy { get; set; }
    public DateTime? RedeemedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}