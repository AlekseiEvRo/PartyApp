using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Полученное игроком достижение. Каталог живёт в коде, здесь — только факт выдачи,
/// чтобы повторно баллы не начислялись.
/// </summary>
public class UserAchievement : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Код достижения из каталога (AchievementCatalog).</summary>
    public string Code { get; set; } = string.Empty;

    public DateTime AwardedAt { get; set; } = DateTime.UtcNow;
}
