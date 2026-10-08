using PartyApp.Domain.Common;

namespace PartyApp.Domain.Entities;

/// <summary>
/// Раунд игры «Шпионы» (один на сессию ивента): кто шпион, оба слова и итог.
/// Горожане видят «слово горожан», шпион — похожее «ложное» слово из той же темы.
/// </summary>
public class SpyFallRound: BaseEntity
{
    public Guid SessionId { get; set; }
    public EventSession Session { get; set; } = null!;

    public Guid SpyUserId { get; set; }
    public User SpyUser { get; set; } = null!;

    /// <summary>Слово, которое видят горожане.</summary>
    public string CitizenWord { get; set; } = string.Empty;

    /// <summary>«Ложное» слово шпиона.</summary>
    public string SpyWord { get; set; } = string.Empty;

    /// <summary>Что ввёл шпион в единственной попытке; null — попытки не было.</summary>
    public string? GuessWord { get; set; }

    public bool GuessCorrect { get; set; }

    /// <summary>Когда попытка была использована; null — ещё можно угадывать.</summary>
    public DateTime? GuessUsedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>"citizens", "spy" или null, пока раунд не завершён.</summary>
    public string? Winner { get; set; }

    /// <summary>"votes", "guess" или "closed" (закрыт без результата).</summary>
    public string? ResolvedBy { get; set; }
}