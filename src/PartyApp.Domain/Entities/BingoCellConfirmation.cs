using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Подтверждение админом, что событие из клетки бинго реально произошло.
/// Только подтверждённые клетки приносят баллы и линии.
/// </summary>
public class BingoCellConfirmation: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    public int CellIndex { get; set; }

    public DateTime ConfirmedAt { get; set; } = DateTime.UtcNow;
}