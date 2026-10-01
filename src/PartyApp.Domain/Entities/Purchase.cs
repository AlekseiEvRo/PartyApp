using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>Покупка приза игроком. Админ отмечает, что приз выдан.</summary>
public class Purchase: BaseEntity
{
    public Guid PlayerId { get; set; }
    public User Player { get; set; } = null!;

    public Guid ShopItemId { get; set; }
    public ShopItem ShopItem { get; set; } = null!;

    /// <summary>Цена на момент покупки.</summary>
    public int Price { get; set; }

    public bool IsFulfilled { get; set; }
    public DateTime? FulfilledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}