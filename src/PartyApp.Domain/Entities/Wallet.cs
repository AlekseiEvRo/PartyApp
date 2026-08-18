using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

public class Wallet: BaseEntity, IHasConcurrency
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public int Balance { get; set; }
    public int Version { get; set; }

    public ICollection<WalletTransaction> Transactions { get; set; } = new List<WalletTransaction>();
}