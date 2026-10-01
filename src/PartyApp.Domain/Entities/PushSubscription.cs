using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Push-подписка устройства (endpoint + ключи, полученные от браузера).
/// У одного пользователя может быть несколько устройств.
/// </summary>
public class PushSubscription : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}