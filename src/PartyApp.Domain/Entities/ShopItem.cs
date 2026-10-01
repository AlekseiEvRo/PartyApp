using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>Приз в магазине: игрок тратит баллы и получает товар.</summary>
public class ShopItem: BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public int Price { get; set; }

    /// <summary>Сколько штук осталось; null — без ограничений.</summary>
    public int? Stock { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}