using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>Журнал действий администраторов: кто, что и над кем сделал.</summary>
public class AdminAuditLog : BaseEntity
{
    public Guid AdminId { get; set; }
    public User Admin { get; set; } = null!;

    public Guid? TargetUserId { get; set; }
    public User? TargetUser { get; set; }

    /// <summary>Код действия: grant_points, role_change, ban и т.д.</summary>
    public string Action { get; set; } = string.Empty;

    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
