using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Лот аукциона с закрытыми ставками: каждый игрок делает одну ставку,
/// баллы удерживаются до закрытия, проигравшим возвращаются.
/// </summary>
public class Lot: BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Минимальная ставка.</summary>
    public int MinBid { get; set; }

    public DateTime EndsAt { get; set; }
    public LotStatus Status { get; set; } = LotStatus.Open;

    public Guid? WinnerId { get; set; }
    public User? Winner { get; set; }
    public int? WinningBid { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Bid> Bids { get; set; } = new List<Bid>();
}