using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Блокировка приёма предсказаний бинго: 1 этап заканчивается, начинается 2 этап —
/// админ ставит клеткам статусы «было»/«не было», а выбор игроков фиксируется.
/// Запись одна на сессию; удаление записи возвращает приём предсказаний.
/// </summary>
public class BingoLock: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    public DateTime LockedAt { get; set; } = DateTime.UtcNow;
}
