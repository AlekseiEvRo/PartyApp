using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

public class EventDefinition: BaseEntity
{
    public string Type { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ConfigJson { get; set; } = "{}";

    public AvailabilityMode Availability { get; set; } = AvailabilityMode.Manual;

    /// <summary>Сколько минут идёт ивент после старта. Пусто — завершается вручную.</summary>
    public int? DurationMinutes { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
}