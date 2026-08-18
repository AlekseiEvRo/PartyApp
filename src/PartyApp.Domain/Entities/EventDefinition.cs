using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

public class EventDefinition: BaseEntity
{
    /// <summary>Ключ для фабрики обработчиков: "quiz", "word_rush", "photo_challenge"...</summary>
    public string Type { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    
    /// <summary>
    /// Гибкий конфиг ивента (вопросы, лимиты времени, баллы и т.д.)
    /// </summary>
    public string ConfigJson { get; set; } = "{}";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
}