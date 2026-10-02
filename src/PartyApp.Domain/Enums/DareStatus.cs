namespace PartyApp.Domain.Enums;

public enum DareStatus
{
    /// <summary>Фант выдан, ждёт подтверждения админом</summary>
    Pending = 0,

    /// <summary>Админ подтвердил выполнение, баллы начислены</summary>
    Confirmed = 10
}