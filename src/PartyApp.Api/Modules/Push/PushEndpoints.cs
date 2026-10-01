using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Push;

public static class PushEndpoints
{
    private const int MaxUserAgentLength = 300;

    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/push").WithTags("Push");

        // Публичный VAPID-ключ, с ним браузер создаёт подписку
        group.MapGet("/vapid-public-key", (IPushNotificationService push) =>
        {
            string? publicKey = push.PublicKey;

            return string.IsNullOrWhiteSpace(publicKey)
                ? Results.Problem("Push-уведомления не настроены на сервере", statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(new { publicKey });
        });

        // Сохраняет (или перепривязывает к текущему пользователю) подписку устройства
        group.MapPost("/subscribe", async (
                SubscribePushRequest request,
                ClaimsPrincipal user,
                AppDbContext db,
                HttpContext http,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                if (!IsValidEndpoint(request.Endpoint))
                    return Results.BadRequest(new { error = "Некорректный endpoint подписки" });

                if (string.IsNullOrWhiteSpace(request.P256dh) || string.IsNullOrWhiteSpace(request.Auth))
                    return Results.BadRequest(new { error = "Не хватает ключей подписки" });

                string? userAgent = string.IsNullOrWhiteSpace(request.UserAgent)
                    ? http.Request.Headers.UserAgent.ToString()
                    : request.UserAgent;

                if (!string.IsNullOrWhiteSpace(userAgent) && userAgent.Length > MaxUserAgentLength)
                    userAgent = userAgent[..MaxUserAgentLength];

                PushSubscription? existing = await db.PushSubscriptions
                    .FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);

                if (existing is null)
                {
                    db.PushSubscriptions.Add(new PushSubscription
                    {
                        UserId = userId,
                        Endpoint = request.Endpoint,
                        P256dh = request.P256dh,
                        Auth = request.Auth,
                        UserAgent = userAgent
                    });
                }
                else
                {
                    ApplySubscriptionChanges(existing, userId, request, userAgent);
                }

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Гонка: ту же подписку сохранили параллельно — обновляем существующую строку
                    db.ChangeTracker.Clear();

                    PushSubscription? raced = await db.PushSubscriptions
                        .FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);

                    if (raced is null)
                        throw;

                    ApplySubscriptionChanges(raced, userId, request, userAgent);
                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok(new { success = true });
            })
            .RequireAuthorization();

        group.MapPost("/unsubscribe", async (
                UnsubscribePushRequest request,
                ClaimsPrincipal user,
                AppDbContext db,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(request.Endpoint))
                    return Results.BadRequest(new { error = "Не указан endpoint подписки" });

                PushSubscription? existing = await db.PushSubscriptions
                    .FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint && s.UserId == userId, ct);

                if (existing is not null)
                {
                    db.PushSubscriptions.Remove(existing);
                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok(new { success = true });
            })
            .RequireAuthorization();

        // Тестовое уведомление — удобно проверять настройки на телефоне
        group.MapPost("/test", async (
                ClaimsPrincipal user,
                IPushNotificationService push,
                CancellationToken ct) =>
            {
                if (!TryGetUserId(user, out Guid userId))
                    return Results.Unauthorized();

                PushDeliveryReport report = await push.SendToUserNowAsync(
                    userId,
                    new PushMessage(
                        Title: "🔔 Проверка уведомлений",
                        Body: "Если ты видишь это сообщение — push работает!",
                        Url: "/",
                        Tag: "push-test"),
                    ct);

                return Results.Ok(report);
            })
            .RequireAuthorization();

        return app;
    }

    private static void ApplySubscriptionChanges(
        PushSubscription subscription,
        Guid userId,
        SubscribePushRequest request,
        string? userAgent)
    {
        subscription.UserId = userId;
        subscription.P256dh = request.P256dh;
        subscription.Auth = request.Auth;
        subscription.UserAgent = userAgent;
        subscription.UpdatedAt = DateTime.UtcNow;
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        string? subClaim = user.FindFirst("sub")?.Value;
        return Guid.TryParse(subClaim, out userId);
    }

    private static bool IsValidEndpoint(string? endpoint)
    {
        return !string.IsNullOrWhiteSpace(endpoint)
            && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && IsAllowedPushHost(uri.Host);
    }

    /// <summary>
    /// Сервер сам шлёт POST на endpoint, поэтому принимаем только адреса
    /// push-сервисов браузеров, а не произвольные URL.
    /// </summary>
    private static bool IsAllowedPushHost(string host)
    {
        string[] allowedSuffixes =
        {
            ".push.apple.com",
            ".push.services.mozilla.com",
            ".notify.windows.com"
        };

        return host.Equals("fcm.googleapis.com", StringComparison.OrdinalIgnoreCase)
            || allowedSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}

public record SubscribePushRequest(string Endpoint, string P256dh, string Auth, string? UserAgent);

public record UnsubscribePushRequest(string Endpoint);