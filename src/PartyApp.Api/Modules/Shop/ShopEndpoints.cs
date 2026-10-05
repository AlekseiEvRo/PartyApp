using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Common.Security;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Shop;

/// <summary>
/// Магазин призов: игроки тратят баллы, админ управляет товарами
/// и отмечает выдачу.
/// </summary>
public static class ShopEndpoints
{
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 500;
    private const int MaxPurchasesPage = 100;

    public static IEndpointRouteBuilder MapShopEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shop")
            .WithTags("Shop")
            .RequireAuthorization();

        // === Игровые ===

        group.MapGet("/items", async (
                ClaimsPrincipal user,
                AppDbContext db,
                bool? includeInactive,
                CancellationToken ct) =>
            {
                IQueryable<ShopItem> query = db.ShopItems.AsNoTracking();

                if (includeInactive == true && user.IsAdminOrSuperAdmin())
                    query = query.Where(i => true);
                else
                    query = query.Where(i => i.IsActive);

                var items = await query
                    .OrderBy(i => i.Price)
                    .ThenBy(i => i.Name)
                    .Select(i => new
                    {
                        i.Id,
                        i.Name,
                        i.Description,
                        i.Price,
                        i.Stock,
                        i.IsActive
                    })
                    .ToListAsync(ct);

                return Results.Ok(items);
            });

        group.MapPost("/items/{itemId:guid}/buy", async (
                Guid itemId,
                ClaimsPrincipal user,
                AppDbContext db,
                IPointsAwardService pointsAward,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                ShopItem? item = await db.ShopItems.AsNoTracking()
                    .SingleOrDefaultAsync(i => i.Id == itemId, ct);

                if (item is null || !item.IsActive)
                    return Results.NotFound(new { error = "Товар не найден" });

                // Резервируем штуку атомарно: два покупателя не возьмут последнюю
                bool limited = item.Stock is not null;
                if (limited)
                {
                    int reserved = await db.ShopItems
                        .Where(i => i.Id == itemId && i.Stock > 0)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(i => i.Stock, i => i.Stock - 1),
                            ct);

                    if (reserved == 0)
                        return Results.Conflict(new { error = "Товар закончился" });
                }

                int? newBalance = await pointsAward.TrySpendAsync(
                    userId,
                    item.Price,
                    $"Покупка: {item.Name}",
                    WalletTransactionType.ShopPurchase,
                    ct: ct);

                if (newBalance is null)
                {
                    // Не хватило баллов — возвращаем зарезервированную штуку
                    if (limited)
                    {
                        await db.ShopItems
                            .Where(i => i.Id == itemId)
                            .ExecuteUpdateAsync(
                                setters => setters.SetProperty(i => i.Stock, i => i.Stock + 1),
                                ct);
                    }

                    return Results.Conflict(new { error = "Недостаточно баллов" });
                }

                var purchase = new Purchase
                {
                    PlayerId = userId,
                    ShopItemId = itemId,
                    Price = item.Price
                };

                db.Purchases.Add(purchase);
                await db.SaveChangesAsync(ct);

                await hub.Clients.All.SendAsync("PurchaseUpdated", new
                {
                    purchaseId = purchase.Id,
                    isFulfilled = false
                }, ct);

                return Results.Ok(new
                {
                    purchaseId = purchase.Id,
                    newBalance
                });
            });

        group.MapGet("/purchases/my", async (
                ClaimsPrincipal user,
                AppDbContext db,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                var purchases = await db.Purchases.AsNoTracking()
                    .Where(p => p.PlayerId == userId)
                    .OrderByDescending(p => p.CreatedAt)
                    .Select(p => new
                    {
                        p.Id,
                        ItemName = p.ShopItem.Name,
                        p.Price,
                        p.IsFulfilled,
                        p.CreatedAt
                    })
                    .ToListAsync(ct);

                return Results.Ok(purchases);
            });

        // === Только для админов ===

        group.MapPost("/items", async (
                ShopItemRequest request,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                string? error = ValidateItem(request, out string name, out string? description);
                if (error is not null)
                    return Results.BadRequest(new { error });

                var item = new ShopItem
                {
                    Name = name,
                    Description = description,
                    Price = request.Price,
                    Stock = request.Stock,
                    IsActive = request.IsActive
                };

                db.ShopItems.Add(item);
                await db.SaveChangesAsync(ct);

                await BroadcastShopUpdatedAsync(hub, item.Id, ct);

                return Results.Created($"/api/shop/items/{item.Id}", item);
            })
            .RequireAuthorization("AdminOnly");

        group.MapPut("/items/{itemId:guid}", async (
                Guid itemId,
                ShopItemRequest request,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                ShopItem? item = await db.ShopItems.SingleOrDefaultAsync(i => i.Id == itemId, ct);
                if (item is null)
                    return Results.NotFound(new { error = "Товар не найден" });

                string? error = ValidateItem(request, out string name, out string? description);
                if (error is not null)
                    return Results.BadRequest(new { error });

                item.Name = name;
                item.Description = description;
                item.Price = request.Price;
                item.Stock = request.Stock;
                item.IsActive = request.IsActive;

                await db.SaveChangesAsync(ct);

                await BroadcastShopUpdatedAsync(hub, item.Id, ct);

                return Results.Ok(item);
            })
            .RequireAuthorization("AdminOnly");

        group.MapDelete("/items/{itemId:guid}", async (
                Guid itemId,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                ShopItem? item = await db.ShopItems.SingleOrDefaultAsync(i => i.Id == itemId, ct);
                if (item is null)
                    return Results.NotFound(new { error = "Товар не найден" });

                int purchaseCount = await db.Purchases.CountAsync(p => p.ShopItemId == itemId, ct);
                if (purchaseCount > 0)
                    return Results.Conflict(new
                    {
                        error = $"Нельзя удалить: товар уже покупали ({purchaseCount}). Деактивируйте его"
                    });

                db.ShopItems.Remove(item);
                await db.SaveChangesAsync(ct);

                await BroadcastShopUpdatedAsync(hub, itemId, ct);

                return Results.Ok(new { success = true });
            })
            .RequireAuthorization("AdminOnly");

        group.MapGet("/purchases", async (
                AppDbContext db,
                int? limit,
                CancellationToken ct) =>
            {
                int take = Math.Clamp(limit ?? MaxPurchasesPage, 1, MaxPurchasesPage);

                var purchases = await db.Purchases.AsNoTracking()
                    .OrderByDescending(p => p.CreatedAt)
                    .Take(take)
                    .Select(p => new
                    {
                        p.Id,
                        ItemName = p.ShopItem.Name,
                        p.Price,
                        p.IsFulfilled,
                        p.CreatedAt,
                        PlayerName = p.Player.DisplayName
                    })
                    .ToListAsync(ct);

                return Results.Ok(purchases);
            })
            .RequireAuthorization("AdminOnly");

        group.MapPost("/purchases/{purchaseId:guid}/fulfill", async (
                Guid purchaseId,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                Purchase? purchase = await db.Purchases.SingleOrDefaultAsync(p => p.Id == purchaseId, ct);
                if (purchase is null)
                    return Results.NotFound(new { error = "Покупка не найдена" });

                purchase.IsFulfilled = true;
                purchase.FulfilledAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                // Игрок сразу видит «выдан» у себя в магазине
                await hub.Clients.All.SendAsync("PurchaseUpdated", new
                {
                    purchaseId = purchase.Id,
                    isFulfilled = true
                }, ct);

                return Results.Ok(new { purchase.Id, purchase.IsFulfilled });
            })
            .RequireAuthorization("AdminOnly");

        return app;
    }

    private static Task BroadcastShopUpdatedAsync(IHubContext<PartyHub> hub, Guid itemId, CancellationToken ct)
    {
        return hub.Clients.All.SendAsync("ShopUpdated", new { itemId }, ct);
    }

    private static string? ValidateItem(ShopItemRequest request, out string name, out string? description)
    {
        name = request.Name?.Trim() ?? string.Empty;
        description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        if (name.Length == 0)
            return "Название не может быть пустым";

        if (name.Length > MaxNameLength)
            return $"Название не длиннее {MaxNameLength} символов";

        if (description is { Length: > MaxDescriptionLength })
            return $"Описание не длиннее {MaxDescriptionLength} символов";

        if (request.Price <= 0)
            return "Цена должна быть больше 0";

        if (request.Stock is < 0)
            return "Количество не может быть отрицательным";

        return null;
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        string? subClaim = user.FindFirst("sub")?.Value;
        return Guid.TryParse(subClaim, out userId);
    }
}

public record ShopItemRequest(string? Name, string? Description, int Price, int? Stock, bool IsActive = true);