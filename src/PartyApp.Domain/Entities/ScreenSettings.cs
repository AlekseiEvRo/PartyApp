using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Настройки большого экрана: сколько секунд показывать каждую секцию
/// в режиме ротации. Хранится одной строкой.
/// </summary>
public class ScreenSettings: BaseEntity
{
    /// <summary>Сколько секунд показывать одно фото в слайдшоу.</summary>
    public int PhotoSeconds { get; set; } = 8;

    /// <summary>Сколько секунд показывать лидерборд в ротации.</summary>
    public int LeaderboardSeconds { get; set; } = 60;

    /// <summary>Сколько секунд показывать магазин в ротации.</summary>
    public int ShopSeconds { get; set; } = 60;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}