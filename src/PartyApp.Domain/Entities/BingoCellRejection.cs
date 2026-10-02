using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Отклонение админом события из клетки бинго: событие реально не произошло.
/// Отклонённые клетки не приносят баллов и освобождают слоты предсказаний.
/// </summary>
public class BingoCellRejection: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    public int CellIndex { get; set; }

    public DateTime RejectedAt { get; set; } = DateTime.UtcNow;
}