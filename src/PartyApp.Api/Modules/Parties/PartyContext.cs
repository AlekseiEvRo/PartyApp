using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Parties;

/// <summary>Помощник для привязки нового контента к активной вечеринке.</summary>
public static class PartyContext
{
    /// <summary>Id активной вечеринки или null, если смены ещё не начинали.</summary>
    public static Task<Guid?> GetActivePartyIdAsync(AppDbContext db, CancellationToken ct = default)
    {
        return db.Parties.AsNoTracking()
            .Where(p => p.Status == PartyStatus.Active)
            .OrderByDescending(p => p.StartedAt)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
    }
}
