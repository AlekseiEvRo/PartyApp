using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Shop;

/// <summary>
/// Аукцион с закрытыми ставками: игроки видят только свою ставку,
/// админ управляет лотами и закрывает их.
/// </summary>
public static class AuctionEndpoints
{
    private const int MaxNameLength = 120;
    private const int MaxDescriptionLength = 500;

    public static IEndpointRouteBuilder MapAuctionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shop/lots")
            .WithTags("Auction")
            .RequireAuthorization();

        // === Игровые ===

        group.MapGet("/", async (
                ClaimsPrincipal user,
                AppDbContext db,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                var lots = await db.Lots.AsNoTracking()
                    .Where(l => l.Status == LotStatus.Open)
                    .OrderBy(l => l.EndsAt)
                    .Select(l => new
                    {
                        l.Id,
                        l.Name,
                        l.Description,
                        l.MinBid,
                        l.EndsAt,
                        BidsCount = l.Bids.Count,
                        // Закрытые ставки: игрок видит только свою
                        MyBid = l.Bids
                            .Where(b => b.PlayerId == userId)
                            .Select(b => (int?)b.Amount)
                            .FirstOrDefault()
                    })
                    .ToListAsync(ct);

                return Results.Ok(lots);
            });

        group.MapPost("/{lotId:guid}/bids", async (
                Guid lotId,
                PlaceBidRequest request,
                ClaimsPrincipal user,
                AuctionService auction,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                BidOutcome outcome = await auction.PlaceBidAsync(lotId, userId, request.Amount, ct);

                return outcome.Success
                    ? Results.Ok(new { outcome.Message, outcome.NewBalance })
                    : Results.Conflict(new { error = outcome.Message });
            });

        // === Только для админов ===

        group.MapGet("/all", async (AppDbContext db, CancellationToken ct) =>
            {
                var lots = await db.Lots.AsNoTracking()
                    .OrderByDescending(l => l.CreatedAt)
                    .Select(l => new
                    {
                        l.Id,
                        l.Name,
                        l.Description,
                        l.MinBid,
                        l.EndsAt,
                        Status = l.Status.ToString(),
                        WinnerName = l.Winner != null ? l.Winner.DisplayName : null,
                        l.WinningBid,
                        Bids = l.Bids
                            .OrderByDescending(b => b.Amount)
                            .Select(b => new
                            {
                                b.Amount,
                                PlayerName = b.Player.DisplayName,
                                b.CreatedAt
                            })
                    })
                    .ToListAsync(ct);

                return Results.Ok(lots);
            })
            .RequireAuthorization("AdminOnly");

        group.MapPost("/", async (
                CreateLotRequest request,
                AppDbContext db,
                TimeProvider timeProvider,
                CancellationToken ct) =>
            {
                string name = request.Name?.Trim() ?? string.Empty;
                string? description = string.IsNullOrWhiteSpace(request.Description)
                    ? null
                    : request.Description.Trim();

                if (name.Length == 0)
                    return Results.BadRequest(new { error = "Название не может быть пустым" });

                if (name.Length > MaxNameLength)
                    return Results.BadRequest(new { error = $"Название не длиннее {MaxNameLength} символов" });

                if (description is { Length: > MaxDescriptionLength })
                    return Results.BadRequest(new { error = $"Описание не длиннее {MaxDescriptionLength} символов" });

                if (request.MinBid <= 0)
                    return Results.BadRequest(new { error = "Минимальная ставка должна быть больше 0" });

                if (request.EndsAt <= timeProvider.GetUtcNow().UtcDateTime)
                    return Results.BadRequest(new { error = "Время окончания должно быть в будущем" });

                var lot = new Lot
                {
                    Name = name,
                    Description = description,
                    MinBid = request.MinBid,
                    EndsAt = request.EndsAt
                };

                db.Lots.Add(lot);
                await db.SaveChangesAsync(ct);

                return Results.Created($"/api/shop/lots/{lot.Id}", lot);
            })
            .RequireAuthorization("AdminOnly");

        group.MapPost("/{lotId:guid}/close", async (
                Guid lotId,
                AuctionService auction,
                CancellationToken ct) =>
            {
                LotCloseOutcome outcome = await auction.CloseLotAsync(lotId, ct);

                return outcome.Success
                    ? Results.Ok(outcome.Data ?? new { success = true })
                    : Results.Conflict(new { error = outcome.Message });
            })
            .RequireAuthorization("AdminOnly");

        group.MapPost("/{lotId:guid}/cancel", async (
                Guid lotId,
                AuctionService auction,
                CancellationToken ct) =>
            {
                LotCloseOutcome outcome = await auction.CancelLotAsync(lotId, ct);

                return outcome.Success
                    ? Results.Ok(new { success = true })
                    : Results.Conflict(new { error = outcome.Message });
            })
            .RequireAuthorization("AdminOnly");

        return app;
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        string? subClaim = user.FindFirst("sub")?.Value;
        return Guid.TryParse(subClaim, out userId);
    }
}

public record PlaceBidRequest(int Amount);

public record CreateLotRequest(string? Name, string? Description, int MinBid, DateTime EndsAt);