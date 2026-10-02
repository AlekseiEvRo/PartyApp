namespace PartyApp.Domain.Enums;

public enum LotStatus
{
    /// <summary>Создан админом, приём ставок ещё не открыт</summary>
    Draft = -10,

    /// <summary>Принимает ставки</summary>
    Open = 0,

    /// <summary>Закрыт, победитель определён</summary>
    Finished = 10,

    /// <summary>Отменён, ставки возвращены</summary>
    Cancelled = 20
}