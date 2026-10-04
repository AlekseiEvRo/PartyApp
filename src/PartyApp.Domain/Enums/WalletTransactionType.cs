namespace PartyApp.Domain.Enums;

public enum WalletTransactionType
{
    EventReward = 0,
    QrBonus = 10,
    AdminGrant = 20,
    AdminDeduct = 30,
    TransferIn = 40,
    TransferOut = 50,
    ShopPurchase = 60,
    AuctionBid = 70,
    Refund = 80,
    PhotoReward = 90
}