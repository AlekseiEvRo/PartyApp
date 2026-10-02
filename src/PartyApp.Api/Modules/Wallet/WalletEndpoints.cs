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

        // Список получателей для перевода (все, кроме себя)
        group.MapGet("/players", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(user, out var userId))
                return Results.Unauthorized();

            var players = await db.Users.AsNoTracking()
                .Where(u => u.Id != userId)
                .OrderBy(u => u.DisplayName)
                .Select(u => new
                {
                    u.Id,
                    u.DisplayName,
                    u.Username
                })
                .ToListAsync(ct);

            return Results.Ok(players);
        });

        // Перевод баллов другому игроку
        group.MapPost("/transfer", async (
            TransferRequest request,
            ClaimsPrincipal user,
            AppDbContext db,
            IPointsAwardService pointsAward,
            CancellationToken ct) =>
        {
            if (!TryGetUserId(user, out var fromUserId))
                return Results.Unauthorized();

            if (request.Amount <= 0)
                return Results.BadRequest(new { error = "Сумма перевода должна быть больше 0" });

            if (request.RecipientId == fromUserId)
                return Results.BadRequest(new { error = "Нельзя перевести баллы самому себе" });

            var recipientExists = await db.Users.AsNoTracking()
                .AnyAsync(u => u.Id == request.RecipientId, ct);

            if (!recipientExists)
                return Results.NotFound(new { error = "Получатель не найден" });

            string? comment = request.Comment?.Trim();
            if (comment?.Length > 200)
                comment = comment[..200];
            if (string.IsNullOrWhiteSpace(comment))
                comment = null;

            TransferOutcome? outcome = await pointsAward.TransferAsync(
                fromUserId, request.RecipientId, request.Amount, comment, ct);

            if (outcome is null)
                return Results.BadRequest(new { error = "Недостаточно баллов для перевода" });

            return Results.Ok(new
            {
                success = true,
                amount = request.Amount,
                newBalance = outcome.SenderBalance,
                recipientBalance = outcome.RecipientBalance
            });
        })
        .RequireRateLimiting("submit");

        return app;
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        var sub = user.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out userId);
    }
}

public record TransferRequest(Guid RecipientId, int Amount, string? Comment);