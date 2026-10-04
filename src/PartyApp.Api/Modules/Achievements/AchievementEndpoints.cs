using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Achievements;

public static class AchievementEndpoints
{
    public static IEndpointRouteBuilder MapAchievementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/achievements")
            .WithTags("Achievements")
            .RequireAuthorization();

        // Каталог достижений с отметками «мои полученные»
        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            Guid userId = Guid.Parse(user.FindFirst("sub")!.Value);

            Dictionary<string, DateTime?> awarded = await db.UserAchievements.AsNoTracking()
                .Where(a => a.UserId == userId)
                .ToDictionaryAsync(a => a.Code, a => (DateTime?)a.AwardedAt, ct);

            var items = AchievementCatalog.All.Select(definition => new
            {
                definition.Code,
                definition.Title,
                definition.Icon,
                definition.Description,
                definition.Points,
                awardedAt = awarded.GetValueOrDefault(definition.Code)
            });

            return Results.Ok(new { items, awardedCount = awarded.Count });
        });

        return app;
    }
}
