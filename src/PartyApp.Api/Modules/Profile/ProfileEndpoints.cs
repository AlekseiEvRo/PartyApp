using System.Security.Claims;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Common.Files;
using PartyApp.Api.Hubs;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Files;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Profile;

/// <summary>
/// Профиль игрока: аватар (файл на диске, в БД — путь) и статус-эмодзи.
/// После изменений всем уходит ProfileUpdated, чтобы клиенты сбросили кэш.
/// </summary>
public static class ProfileEndpoints
{
    private const int MaxStatusEmojiLength = 16;

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .WithTags("Profile")
            .RequireAuthorization();

        group.MapGet("/profile", async (
            ClaimsPrincipal user,
            AppDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            Guid userId = GetUserId(user);

            var profile = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.DisplayName,
                    u.StatusEmoji,
                    HasAvatar = u.AvatarPath != null,
                    u.ProfileUpdatedAt
                })
                .SingleOrDefaultAsync(ct);

            if (profile is null)
                return Results.Unauthorized();

            return Results.Ok(new
            {
                userId = profile.Id,
                profile.Username,
                profile.DisplayName,
                profile.StatusEmoji,
                profile.HasAvatar,
                profile.ProfileUpdatedAt,
                maxAvatarBytes = config.GetValue("Profile:MaxAvatarBytes", 2 * 1024 * 1024)
            });
        });

        group.MapPatch("/profile", async (
            UpdateProfileRequest request,
            ClaimsPrincipal user,
            AppDbContext db,
            IHubContext<PartyHub> hub,
            CancellationToken ct) =>
        {
            string? statusEmoji = request.StatusEmoji?.Trim();
            if (statusEmoji is { Length: > MaxStatusEmojiLength })
                return Results.BadRequest(new { error = $"Статус не длиннее {MaxStatusEmojiLength} символов" });

            if (statusEmoji?.Length == 0)
                statusEmoji = null;

            User? player = await db.Users.SingleOrDefaultAsync(u => u.Id == GetUserId(user), ct);
            if (player is null)
                return Results.Unauthorized();

            player.StatusEmoji = statusEmoji;
            player.ProfileUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await BroadcastProfileAsync(hub, player, ct);
            return Results.Ok(new { statusEmoji, profileUpdatedAt = player.ProfileUpdatedAt });
        });

        group.MapPost("/profile/avatar", async (
            IFormFile file,
            ClaimsPrincipal user,
            AppDbContext db,
            IFileStorage storage,
            IConfiguration config,
            IHubContext<PartyHub> hub,
            CancellationToken ct) =>
        {
            int maxBytes = config.GetValue("Profile:MaxAvatarBytes", 2 * 1024 * 1024);
            if (file.Length == 0)
                return Results.BadRequest(new { error = "Файл пустой" });

            if (file.Length > maxBytes)
                return Results.BadRequest(new { error = $"Файл больше {maxBytes / (1024 * 1024)} МБ" });

            string? contentType = await ImageContent.DetectAsync(file, ct);
            if (contentType is null)
                return Results.BadRequest(new { error = "Поддерживаются только JPEG, PNG и WebP" });

            User? player = await db.Users.SingleOrDefaultAsync(u => u.Id == GetUserId(user), ct);
            if (player is null)
                return Results.Unauthorized();

            await using Stream content = file.OpenReadStream();
            string path = await storage.SaveAsync(content, ImageContent.ExtensionFor(contentType), "avatars", ct);

            string? oldPath = player.AvatarPath;
            player.AvatarPath = path;
            player.ProfileUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            if (!string.IsNullOrEmpty(oldPath))
                storage.Delete(oldPath);

            await BroadcastProfileAsync(hub, player, ct);
            return Results.Ok(new { hasAvatar = true, profileUpdatedAt = player.ProfileUpdatedAt });
        })
        .RequireRateLimiting("submit")
        .DisableAntiforgery();

        group.MapDelete("/profile/avatar", async (
            ClaimsPrincipal user,
            AppDbContext db,
            IFileStorage storage,
            IHubContext<PartyHub> hub,
            CancellationToken ct) =>
        {
            User? player = await db.Users.SingleOrDefaultAsync(u => u.Id == GetUserId(user), ct);
            if (player is null)
                return Results.Unauthorized();

            string? oldPath = player.AvatarPath;
            if (oldPath is not null)
            {
                player.AvatarPath = null;
                player.ProfileUpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                storage.Delete(oldPath);
                await BroadcastProfileAsync(hub, player, ct);
            }

            return Results.Ok(new { hasAvatar = false });
        });

        // Отдача аватара: файл лежит на диске, в БД только путь
        group.MapGet("/users/{userId:guid}/avatar", async (
            Guid userId,
            AppDbContext db,
            IFileStorage storage,
            CancellationToken ct) =>
        {
            string? path = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.AvatarPath)
                .SingleOrDefaultAsync(ct);

            if (string.IsNullOrEmpty(path))
                return Results.NotFound();

            Stream? stream = storage.OpenRead(path);
            if (stream is null)
                return Results.NotFound();

            return Results.Stream(stream, ImageContent.ContentTypeFor(path));
        });

        return app;
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        return Guid.Parse(user.FindFirst("sub")!.Value);
    }

    private static Task BroadcastProfileAsync(IHubContext<PartyHub> hub, User player, CancellationToken ct)
    {
        return hub.Clients.All.SendAsync("ProfileUpdated", new
        {
            userId = player.Id,
            profileUpdatedAt = player.ProfileUpdatedAt
        }, ct);
    }
}

public record UpdateProfileRequest(string? StatusEmoji);
