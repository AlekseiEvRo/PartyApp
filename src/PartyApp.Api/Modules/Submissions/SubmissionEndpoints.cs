using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Moderation;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Submissions;

/// <summary>
/// Стенка пожеланий: игроки пишут тосты и пожелания, админ модерирует,
/// одобренное уходит на общий экран.
/// </summary>
public static class SubmissionEndpoints
{
    private const int MaxPageSize = 100;
    private const int MaxTextLength = 500;

    public static IEndpointRouteBuilder MapSubmissionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wishes")
            .WithTags("Wishes")
            .RequireAuthorization();

        // === Игровые ===

        group.MapPost("/", async (
                SubmitWishRequest request,
                ClaimsPrincipal user,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                ModerationNotifier notifier,
                IConfiguration config,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                string text = request.Text?.Trim() ?? string.Empty;
                if (text.Length == 0)
                    return Results.BadRequest(new { error = "Пожелание не может быть пустым" });

                if (text.Length > MaxTextLength)
                    return Results.BadRequest(new { error = $"Пожелание не длиннее {MaxTextLength} символов" });

                bool requireModeration = config.GetValue("Wishes:RequireModeration", true);

                var wish = new Wish
                {
                    PlayerId = userId,
                    Text = text,
                    Status = requireModeration ? ModerationStatus.Pending : ModerationStatus.Approved,
                    CreatedAt = DateTime.UtcNow
                };

                db.Wishes.Add(wish);
                await db.SaveChangesAsync(ct);

                if (wish.Status == ModerationStatus.Pending)
                {
                    string authorName = user.FindFirst("displayName")?.Value ?? "Гость";
                    await notifier.NotifyPendingAsync("wish", wish.Id, authorName, wish.Text, ct);
                }
                else
                {
                    await BroadcastWishAsync(hub, wish, user, ct);
                }

                return Results.Created($"/api/wishes/{wish.Id}", new
                {
                    wish.Id,
                    status = wish.Status.ToString()
                });
            })
            .RequireRateLimiting("submit");

        group.MapGet("/", async (
                ClaimsPrincipal user,
                AppDbContext db,
                int? limit,
                int? offset,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                int take = Math.Clamp(limit ?? 50, 1, MaxPageSize);
                int skip = Math.Max(offset ?? 0, 0);

                IQueryable<Wish> query = db.Wishes.AsNoTracking();

                if (!user.IsInRole("Admin"))
                {
                    // Игрок видит одобренные пожелания и свои собственные
                    query = query.Where(w =>
                        w.Status == ModerationStatus.Approved || w.PlayerId == userId);
                }

                int total = await query.CountAsync(ct);

                var items = await query
                    .OrderByDescending(w => w.CreatedAt)
                    .Skip(skip)
                    .Take(take)
                    .Select(w => new
                    {
                        w.Id,
                        PlayerName = w.Player.DisplayName,
                        w.Text,
                        Status = w.Status.ToString(),
                        w.CreatedAt,
                        IsMine = w.PlayerId == userId
                    })
                    .ToListAsync(ct);

                return Results.Ok(new { items, total });
            });

        group.MapDelete("/{wishId:guid}", async (
                Guid wishId,
                ClaimsPrincipal user,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                Wish? wish = await db.Wishes.SingleOrDefaultAsync(w => w.Id == wishId, ct);
                if (wish is null)
                    return Results.NotFound();

                if (wish.PlayerId != userId && !user.IsInRole("Admin"))
                    return Results.Forbid();

                db.Wishes.Remove(wish);
                await db.SaveChangesAsync(ct);

                // Чтобы пожелание исчезло у всех открытых приложений без перезагрузки
                await BroadcastWishRemovedAsync(hub, wishId, ct);

                return Results.Ok(new { success = true });
            });

        // === Только для админов ===

        group.MapPost("/{wishId:guid}/approve", (
                Guid wishId,
                ClaimsPrincipal user,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            ModerateAsync(wishId, ModerationStatus.Approved, user, db, hub, ct))
            .RequireAuthorization("AdminOnly");

        group.MapPost("/{wishId:guid}/reject", (
                Guid wishId,
                ClaimsPrincipal user,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            ModerateAsync(wishId, ModerationStatus.Rejected, user, db, hub, ct))
            .RequireAuthorization("AdminOnly");

        return app;
    }

    private static async Task<IResult> ModerateAsync(
        Guid wishId,
        ModerationStatus status,
        ClaimsPrincipal user,
        AppDbContext db,
        IHubContext<PartyHub> hub,
        CancellationToken ct)
    {
        Wish? wish = await db.Wishes.SingleOrDefaultAsync(w => w.Id == wishId, ct);
        if (wish is null)
            return Results.NotFound();

        bool wasApproved = wish.Status == ModerationStatus.Approved;

        wish.Status = status;
        await db.SaveChangesAsync(ct);

        if (status == ModerationStatus.Approved)
        {
            await BroadcastWishAsync(hub, wish, user, ct);
        }
        else if (wasApproved)
        {
            // Одобренное пожелание отозвано — убираем его со стенки
            await BroadcastWishRemovedAsync(hub, wish.Id, ct);
        }

        return Results.Ok(new { wish.Id, status = wish.Status.ToString() });
    }

    private static Task BroadcastWishRemovedAsync(
        IHubContext<PartyHub> hub,
        Guid wishId,
        CancellationToken ct)
    {
        return hub.Clients.All.SendAsync("WishRemoved", new { wishId }, ct);
    }

    private static Task BroadcastWishAsync(
        IHubContext<PartyHub> hub,
        Wish wish,
        ClaimsPrincipal author,
        CancellationToken ct)
    {
        string playerName = author.FindFirst("displayName")?.Value ?? "Гость";

        return hub.Clients.All.SendAsync("WishAdded", new
        {
            wishId = wish.Id,
            playerName,
            wish.Text,
            wish.CreatedAt
        }, ct);
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        string? subClaim = user.FindFirst("sub")?.Value;
        return Guid.TryParse(subClaim, out userId);
    }
}

public record SubmitWishRequest(string? Text);