namespace PartyApp.Domain.Enums;

public enum LotStatus
{
    /// <summary>Принимает ставки</summary>
    Open = 0,

    /// <summary>Закрыт, победитель определён</summary>
    Finished = 10,

    /// <summary>Отменён, ставки возвращены</summary>
    Cancelled = 20
}