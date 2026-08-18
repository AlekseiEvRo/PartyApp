using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

public class WalletTransaction: BaseEntity
{
    public Guid WalletId { get; set; }
    public Wallet Wallet { get; set; } = null!;

    public int Amount { get; set; } // может быть отрицательным
    public WalletTransactionType Type { get; set; }
    public string Description { get; set; } = string.Empty;

    public Guid? RelatedSessionId { get; set; }
    public EventSession? RelatedSession { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}