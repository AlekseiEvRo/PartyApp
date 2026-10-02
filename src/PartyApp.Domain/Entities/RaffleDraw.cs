using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>Результат розыгрыша лототрона: победитель выбирается один раз на сессию.</summary>
public class RaffleDraw: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    public Guid WinnerId { get; set; }
    public User Winner { get; set; } = null!;

    public DateTime DrawnAt { get; set; } = DateTime.UtcNow;
}