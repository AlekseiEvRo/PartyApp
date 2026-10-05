using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

public class PlayerSubmission: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    public Guid PlayerId { get; set; }
    public User Player { get; set; } = null!;

    /// <summary>Ответ игрока в гибком формате (зависит от типа ивента)</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Время реакции в мс для ивентов на скорость («Кто быстрее»); иначе null.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Номер билета лототрона (уникален в рамках сессии); иначе null.</summary>
    public int? TicketNumber { get; set; }

    public int? Score { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}