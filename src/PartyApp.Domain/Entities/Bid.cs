using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>Ставка игрока на лот. Одна ставка на игрока, можно повышать.</summary>
public class Bid: BaseEntity
{
    public Guid LotId { get; set; }
    public Lot Lot { get; set; } = null!;

    public Guid PlayerId { get; set; }
    public User Player { get; set; } = null!;

    /// <summary>Сколько баллов удерживается до закрытия лота.</summary>
    public int Amount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}