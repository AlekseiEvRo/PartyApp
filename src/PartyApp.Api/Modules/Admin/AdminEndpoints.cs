using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Admin;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        // Список игроков с балансами
        group.MapGet("/players", async (AppDbContext db, CancellationToken ct) =>
        {
            var players = await db.Users
                .Include(u => u.Wallet)
                .OrderByDescending(u => u.Wallet.Balance)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.DisplayName,
                    Role = u.Role.ToString(),
                    Balance = u.Wallet.Balance,
                    u.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(players);
        });

        // Лидерборд
        group.MapGet("/leaderboard", async (AppDbContext db, CancellationToken ct) =>
        {
            var leaderboard = await db.Users
                .Include(u => u.Wallet)
                .OrderByDescending(u => u.Wallet.Balance)
                .Take(20)
                .Select(u => new
                {
                    u.Id,
                    u.DisplayName,
                    Balance = u.Wallet.Balance
                })
                .ToListAsync(ct);

            return Results.Ok(leaderboard);
        });

        // Ручное начисление/списание баллов
        group.MapPost("/grant-points", async (
            GrantPointsRequest request,
            ClaimsPrincipal admin,
            AppDbContext db,
            PointsAwardService pointsAward,
            CancellationToken ct) =>
        {
            var playerId = request.PlayerId;
            var amount = request.Amount;
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Ручное начисление" : request.Reason;

            if (amount == 0)
                return Results.BadRequest(new { error = "Сумма не может быть 0" });

            var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == playerId, ct);
            if (wallet is null)
                return Results.NotFound(new { error = "Кошелёк игрока не найден" });

            if (amount < 0 && wallet.Balance + amount < 0)
                return Results.BadRequest(new { error = "Недостаточно баллов для списания у игрока" });

            var transactionType = amount > 0
                ? WalletTransactionType.AdminGrant
                : WalletTransactionType.AdminDeduct;

            var newBalance = await pointsAward.AwardAsync(playerId, amount, reason, transactionType, ct: ct);

            return Results.Ok(new
            {
                success = true,
                playerId,
                amount,
                newBalance,
                reason
            });
        });

        // Список сессий ивентов
        group.MapGet("/sessions", async (AppDbContext db, CancellationToken ct) =>
        {
            var sessions = await db.EventSessions
                .Include(s => s.Definition)
                .Include(s => s.StartedBy)
                .OrderByDescending(s => s.StartedAt)
                .Select(s => new
                {
                    s.Id,
                    DefinitionName = s.Definition.DisplayName,
                    Type = s.Definition.Type,
                    State = s.State.ToString(),
                    StartedAt = s.StartedAt,
                    EndedAt = s.EndedAt,
                    StartedBy = s.StartedBy.DisplayName,
                    SubmissionCount = s.Submissions.Count
                })
                .ToListAsync(ct);

            return Results.Ok(sessions);
        });

        return app;
    }
}

public record GrantPointsRequest(Guid PlayerId, int Amount, string? Reason);