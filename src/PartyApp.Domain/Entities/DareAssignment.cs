using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Выданный фант: у игрока — ровно один на сессию. Баллы начисляются,
/// только когда админ подтвердит выполнение.
/// </summary>
public class DareAssignment: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    public Guid PlayerId { get; set; }
    public User Player { get; set; } = null!;

    public int TaskIndex { get; set; }
    public string Task { get; set; } = string.Empty;

    public DareStatus Status { get; set; } = DareStatus.Pending;

    /// <summary>Баллы, которые получит игрок после подтверждения.</summary>
    public int Points { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; set; }
}