using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Moderation;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Files;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Photos;

/// <summary>
/// Фотоальбом вечеринки: загрузка с телефонов, лента, лайки и модерация.
/// Файлы лежат на диске, в БД — только метаданные.
/// </summary>
public static class PhotoEndpoints
{
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/photos")
            .WithTags("Photos")
            .RequireAuthorization();

        // === Игровые ===

        group.MapPost("/", async (
                IFormFile file,
                ClaimsPrincipal user,
                AppDbContext db,
                IFileStorage storage,
                IHubContext<PartyHub> hub,
                ModerationNotifier notifier,
                IConfiguration config,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                int maxSizeBytes = config.GetValue("Photos:MaxSizeBytes", 10 * 1024 * 1024);
                if (file.Length == 0)
                    return Results.BadRequest(new { error = "Файл пустой" });

                if (file.Length > maxSizeBytes)
                    return Results.BadRequest(new
                    {
                        error = $"Файл больше {maxSizeBytes / (1024 * 1024)} МБ"
                    });

                string? contentType = await DetectContentTypeAsync(file, ct);
                if (contentType is null)
                    return Results.BadRequest(new { error = "Поддерживаются только JPEG, PNG и WebP" });

                bool requireModeration = config.GetValue("Photos:RequireModeration", true);

                await using Stream content = file.OpenReadStream();
                string storagePath = await storage.SaveAsync(content, ExtensionFor(contentType), ct);

                var photo = new PartyPhoto
                {
                    UploadedById = userId,
                    StoragePath = storagePath,
                    OriginalFileName = NormalizeFileName(file.FileName),
                    ContentType = contentType,
                    SizeBytes = file.Length,
                    Status = requireModeration ? ModerationStatus.Pending : ModerationStatus.Approved,
                    UploadedAt = DateTime.UtcNow
                };

                db.PartyPhotos.Add(photo);
                await db.SaveChangesAsync(ct);

                if (photo.Status == ModerationStatus.Pending)
                {
                    string authorName = user.FindFirst("displayName")?.Value ?? "Гость";
                    await notifier.NotifyPendingAsync("photo", photo.Id, authorName, null, ct);
                }
                else
                {
                    await BroadcastPhotoAsync(hub, photo, user, ct);
                }

                return Results.Created($"/api/photos/{photo.Id}", new
                {
                    photo.Id,
                    status = photo.Status.ToString()
                });
            })
            .DisableAntiforgery();

        group.MapGet("/", async (
                ClaimsPrincipal user,
                AppDbContext db,
                bool? mine,
                int? limit,
                int? offset,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                int take = Math.Clamp(limit ?? 30, 1, MaxPageSize);
                int skip = Math.Max(offset ?? 0, 0);

                IQueryable<PartyPhoto> query = db.PartyPhotos.AsNoTracking();

                if (mine == true)
                {
                    query = query.Where(p => p.UploadedById == userId);
                }
                else if (!user.IsInRole("Admin"))
                {
                    // Игрок видит одобренные фото и свои (в том числе на модерации)
                    query = query.Where(p =>
                        p.Status == ModerationStatus.Approved || p.UploadedById == userId);
                }

                int total = await query.CountAsync(ct);

                var items = await query
                    .OrderByDescending(p => p.UploadedAt)
                    .Skip(skip)
                    .Take(take)
                    .Select(p => new
                    {
                        p.Id,
                        UploadedByName = p.UploadedBy.DisplayName,
                        p.UploadedAt,
                        Status = p.Status.ToString(),
                        LikesCount = p.Likes.Count,
                        LikedByMe = p.Likes.Any(l => l.UserId == userId),
                        IsMine = p.UploadedById == userId
                    })
                    .ToListAsync(ct);

                return Results.Ok(new { items, total });
            });

        group.MapGet("/{photoId:guid}/content", async (
                Guid photoId,
                ClaimsPrincipal user,
                AppDbContext db,
                IFileStorage storage,
                HttpContext http,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                PartyPhoto? photo = await db.PartyPhotos.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.Id == photoId, ct);

                if (photo is null || !CanSee(photo, userId, user))
                    return Results.NotFound();

                Stream? stream = storage.OpenRead(photo.StoragePath);
                if (stream is null)
                    return Results.NotFound(new { error = "Файл не найден" });

                // Файлы неизменяемы, поэтому можно кэшировать надолго
                http.Response.Headers.CacheControl = "private, max-age=86400";

                return Results.File(stream, photo.ContentType, enableRangeProcessing: true);
            });

        group.MapDelete("/{photoId:guid}", async (
                Guid photoId,
                ClaimsPrincipal user,
                AppDbContext db,
                IFileStorage storage,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                PartyPhoto? photo = await db.PartyPhotos
                    .SingleOrDefaultAsync(p => p.Id == photoId, ct);

                if (photo is null)
                    return Results.NotFound();

                if (photo.UploadedById != userId && !user.IsInRole("Admin"))
                    return Results.Forbid();

                db.PartyPhotos.Remove(photo);
                await db.SaveChangesAsync(ct);
                storage.Delete(photo.StoragePath);

                // Чтобы фото исчезло у всех открытых приложений без перезагрузки
                await BroadcastPhotoRemovedAsync(hub, photoId, ct);

                return Results.Ok(new { success = true });
            });

        group.MapPost("/{photoId:guid}/like", async (
                Guid photoId,
                ClaimsPrincipal user,
                AppDbContext db,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                PartyPhoto? photo = await db.PartyPhotos.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.Id == photoId, ct);

                if (photo is null || !CanSee(photo, userId, user))
                    return Results.NotFound();

                PhotoLike? like = await db.PhotoLikes
                    .SingleOrDefaultAsync(l => l.PhotoId == photoId && l.UserId == userId, ct);

                bool liked;
                if (like is null)
                {
                    db.PhotoLikes.Add(new PhotoLike { PhotoId = photoId, UserId = userId });
                    liked = true;
                }
                else
                {
                    db.PhotoLikes.Remove(like);
                    liked = false;
                }

                await db.SaveChangesAsync(ct);

                int likesCount = await db.PhotoLikes.CountAsync(l => l.PhotoId == photoId, ct);

                return Results.Ok(new { liked, likesCount });
            });

        // === Только для админов ===

        group.MapPost("/{photoId:guid}/approve", (
                Guid photoId,
                ClaimsPrincipal user,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            ModerateAsync(photoId, ModerationStatus.Approved, user, db, hub, ct))
            .RequireAuthorization("AdminOnly");

        group.MapPost("/{photoId:guid}/reject", (
                Guid photoId,
                ClaimsPrincipal user,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            ModerateAsync(photoId, ModerationStatus.Rejected, user, db, hub, ct))
            .RequireAuthorization("AdminOnly");

        return app;
    }

    private static async Task<IResult> ModerateAsync(
        Guid photoId,
        ModerationStatus status,
        ClaimsPrincipal user,
        AppDbContext db,
        IHubContext<PartyHub> hub,
        CancellationToken ct)
    {
        PartyPhoto? photo = await db.PartyPhotos
            .SingleOrDefaultAsync(p => p.Id == photoId, ct);

        if (photo is null)
            return Results.NotFound();

        bool wasApproved = photo.Status == ModerationStatus.Approved;

        photo.Status = status;
        await db.SaveChangesAsync(ct);

        if (status == ModerationStatus.Approved)
        {
            await BroadcastPhotoAsync(hub, photo, user, ct);
        }
        else if (wasApproved)
        {
            // Одобренное фото отозвано — убираем его из лент
            await BroadcastPhotoRemovedAsync(hub, photo.Id, ct);
        }

        return Results.Ok(new { photo.Id, status = photo.Status.ToString() });
    }

    private static Task BroadcastPhotoRemovedAsync(
        IHubContext<PartyHub> hub,
        Guid photoId,
        CancellationToken ct)
    {
        return hub.Clients.All.SendAsync("PhotoRemoved", new { photoId }, ct);
    }

    private static Task BroadcastPhotoAsync(
        IHubContext<PartyHub> hub,
        PartyPhoto photo,
        ClaimsPrincipal uploader,
        CancellationToken ct)
    {
        string uploaderName = uploader.FindFirst("displayName")?.Value ?? "Кто-то";

        return hub.Clients.All.SendAsync("PhotoUploaded", new
        {
            photoId = photo.Id,
            uploadedByName = uploaderName,
            uploadedAt = photo.UploadedAt
        }, ct);
    }

    private static bool CanSee(PartyPhoto photo, Guid userId, ClaimsPrincipal user)
    {
        return photo.Status == ModerationStatus.Approved
            || photo.UploadedById == userId
            || user.IsInRole("Admin");
    }

    /// <summary>Определяет тип по сигнатуре файла, а не по данным клиента.</summary>
    private static async Task<string?> DetectContentTypeAsync(IFormFile file, CancellationToken ct)
    {
        byte[] header = new byte[12];

        await using Stream stream = file.OpenReadStream();
        int read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);

        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return "image/jpeg";

        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return "image/png";

        if (read >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return "image/webp";

        return null;
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    private static string NormalizeFileName(string? fileName)
    {
        string name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length == 0)
            return "photo";

        return name.Length > 255 ? name[..255] : name;
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        string? subClaim = user.FindFirst("sub")?.Value;
        return Guid.TryParse(subClaim, out userId);
    }
}