using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Вечеринка: смена с обнулением баллов, своим списком ивентов и итогами.
/// До создания первой вечеринки приложение работает как раньше — лидерборд общий.
/// </summary>
public class Party : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public PartyStatus Status { get; set; } = PartyStatus.Active;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
