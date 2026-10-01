namespace PartyApp.Domain.Enums;

public enum ModerationStatus
{
    /// <summary>Ждёт проверки администратором</summary>
    Pending = 0,

    /// <summary>Одобрено, видно всем</summary>
    Approved = 10,

    /// <summary>Отклонено администратором</summary>
    Rejected = 20
}