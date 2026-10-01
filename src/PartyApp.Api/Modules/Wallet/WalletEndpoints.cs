using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Wallet;

public static class WalletEndpoints
{
    private const int MaxTransactionsPageSize = 100;

    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wallet")
            .WithTags("Wallet")
            .RequireAuthorization();

        group.MapGet("/balance", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(user, out var userId))
                return Results.Unauthorized();

            var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == userId, ct);

            return Results.Ok(new { balance = wallet?.Balance ?? 0 });
        });

        // История начислений и списаний, новые сверху
        group.MapGet("/transactions", async (
            ClaimsPrincipal user,
            AppDbContext db,
            int? limit,
            int? offset,
            CancellationToken ct) =>
        {
            if (!TryGetUserId(user, out var userId))
                return Results.Unauthorized();

            var wallet = await db.Wallets.AsNoTracking()
                .SingleOrDefaultAsync(w => w.UserId == userId, ct);

            if (wallet is null)
                return Results.Ok(new { items = Array.Empty<object>(), total = 0 });

            var take = Math.Clamp(limit ?? 50, 1, MaxTransactionsPageSize);
            var skip = Math.Max(offset ?? 0, 0);

            var query = db.WalletTransactions.AsNoTracking()
                .Where(t => t.WalletId == wallet.Id);

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(t => t.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Select(t => new
                {
                    t.Id,
                    t.Amount,
                    Type = t.Type.ToString(),
                    t.Description,
                    t.RelatedSessionId,
                    t.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(new { items, total });
        });

        return app;
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        var sub = user.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out userId);
    }
}