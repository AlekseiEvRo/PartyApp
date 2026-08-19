using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Wallet;

public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wallet")
            .WithTags("Wallet")
            .RequireAuthorization();

        group.MapGet("/balance", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var sub = user.FindFirst("sub")?.Value;
            if (sub is null || !Guid.TryParse(sub, out var userId))
                return Results.Unauthorized();

            var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == userId, ct);

            return Results.Ok(new { balance = wallet?.Balance ?? 0 });
        });

        return app;
    }
}