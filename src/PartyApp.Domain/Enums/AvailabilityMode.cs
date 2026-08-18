namespace PartyApp.Domain.Enums;

public enum AvailabilityMode
{
    /// <summary>Запускается админом вручную</summary>
    Manual = 0,

    /// <summary>Доступен всё время, пока вечеринка активна</summary>
    AlwaysOn = 10,

    /// <summary>Запускается по расписанию</summary>
    Scheduled = 20
}