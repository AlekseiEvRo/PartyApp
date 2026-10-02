using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Награды за контент. Хранится одной строкой, правится в админке.
/// </summary>
public class RewardSettings: BaseEntity
{
    /// <summary>Сколько баллов получает автор за одобренное фото.</summary>
    public int PhotoApprovedPoints { get; set; } = 5;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}