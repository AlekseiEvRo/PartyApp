using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Начисление бонуса за собранную линию бинго. Каждая линия разыгрывается один
/// раз: бонус получает самый быстрый — тот, кто раньше всех закрыл линию отметками.
/// </summary>
public class BingoLineAward: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    /// <summary>Номер линии в порядке строк/столбцов/диагоналей (см. BingoService).</summary>
    public int LineIndex { get; set; }

    public Guid PlayerId { get; set; }
    public User Player { get; set; } = null!;

    public int Amount { get; set; }

    public DateTime AwardedAt { get; set; } = DateTime.UtcNow;
}