using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Screen;

/// <summary>
/// Управление большим экраном: админ переключает режимы, состояние уходит
/// всем подключённым клиентам событием ScreenUpdated.
/// </summary>
public static class ScreenEndpoints
{
    private const int MaxMessageLength = 200;
    private const int MaxReactionTextLength = 120;

    public static IEndpointRouteBuilder MapScreenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/screen")
            .WithTags("Screen")
            .RequireAuthorization();

        group.MapGet("/state", (ScreenService screen) => Results.Ok(screen.GetState()));

        group.MapGet("/settings", async (AppDbContext db, CancellationToken ct) =>
        {
            ScreenSettings? settings = await db.ScreenSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            return Results.Ok(ToSettingsDto(settings));
        });

        group.MapPost("/state", async (
                SetScreenStateRequest request,
                ScreenService screen,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                string mode = request.Mode?.Trim().ToLowerInvariant() ?? string.Empty;
                if (!ScreenModes.All.Contains(mode))
                    return Results.BadRequest(new { error = $"Неизвестный режим экрана: {request.Mode}" });

                Guid? sessionId = null;
                if (mode == ScreenModes.Event)
                {
                    if (request.SessionId is null)
                        return Results.BadRequest(new { error = "Для режима ивента нужен sessionId" });

                    bool exists = await db.EventSessions.AnyAsync(s => s.Id == request.SessionId, ct);
                    if (!exists)
                        return Results.NotFound(new { error = "Сессия ивента не найдена" });

                    sessionId = request.SessionId;
                }

                string? message = null;
                if (mode == ScreenModes.Message)
                {
                    message = request.Message?.Trim();
                    if (string.IsNullOrEmpty(message))
                        return Results.BadRequest(new { error = "Сообщение не может быть пустым" });

                    if (message.Length > MaxMessageLength)
                        message = message[..MaxMessageLength];
                }

                ScreenStateDto state = screen.SetState(mode, sessionId, message);
                await hub.Clients.All.SendAsync("ScreenUpdated", state, ct);

                return Results.Ok(state);
            })
            .RequireAuthorization("AdminOnly");

        group.MapPost("/confetti", async (
                ScreenService screen,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                ConfettiDto confetti = screen.FireConfetti();
                await hub.Clients.All.SendAsync("ScreenConfetti", confetti, ct);

                return Results.Ok(confetti);
            })
            .RequireRateLimiting("submit");

        // Стикеры и подписи от игроков: всплывают на экране поверх всего
        group.MapPost("/reactions", async (
                SendReactionRequest request,
                ClaimsPrincipal user,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                string? emoji = request.Emoji?.Trim();
                if (emoji is { Length: > 16 })
                    return Results.BadRequest(new { error = "Стикер слишком длинный" });

                string? text = request.Text?.Trim();
                if (text is { Length: > MaxReactionTextLength })
                    text = text[..MaxReactionTextLength];

                if (string.IsNullOrEmpty(emoji) && string.IsNullOrEmpty(text))
                    return Results.BadRequest(new { error = "Нужен стикер или текст" });

                var reaction = new ScreenReactionDto(
                    Id: Guid.NewGuid(),
                    Emoji: string.IsNullOrEmpty(emoji) ? null : emoji,
                    Text: string.IsNullOrEmpty(text) ? null : text,
                    AuthorName: user.FindFirst("displayName")?.Value ?? "Гость",
                    CreatedAt: DateTime.UtcNow);

                await hub.Clients.All.SendAsync("ScreenReaction", reaction, ct);

                return Results.Ok(reaction);
            })
            .RequireRateLimiting("submit");

        // Настройки ротации секций на экране
        group.MapPut("/settings", async (
                ScreenSettingsRequest request,
                AppDbContext db,
                IHubContext<PartyHub> hub,
                CancellationToken ct) =>
            {
                if (request.PhotoSeconds is < 3 or > 600)
                    return Results.BadRequest(new { error = "Время показа фото — от 3 до 600 секунд" });

                if (request.LeaderboardSeconds is < 5 or > 3600)
                    return Results.BadRequest(new { error = "Время лидерборда — от 5 до 3600 секунд" });

                if (request.ShopSeconds is < 5 or > 3600)
                    return Results.BadRequest(new { error = "Время магазина — от 5 до 3600 секунд" });

                ScreenSettings? settings = await db.ScreenSettings.FirstOrDefaultAsync(ct);
                if (settings is null)
                {
                    settings = new ScreenSettings();
                    db.ScreenSettings.Add(settings);
                }

                settings.PhotoSeconds = request.PhotoSeconds;
                settings.LeaderboardSeconds = request.LeaderboardSeconds;
                settings.ShopSeconds = request.ShopSeconds;
                settings.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                object dto = ToSettingsDto(settings);
                await hub.Clients.All.SendAsync("ScreenSettingsUpdated", dto, ct);

                return Results.Ok(dto);
            })
            .RequireAuthorization("AdminOnly");

        return app;
    }

    private static object ToSettingsDto(ScreenSettings? settings) => new
    {
        photoSeconds = settings?.PhotoSeconds ?? 8,
        leaderboardSeconds = settings?.LeaderboardSeconds ?? 60,
        shopSeconds = settings?.ShopSeconds ?? 60
    };
}

public record SetScreenStateRequest(string? Mode, Guid? SessionId, string? Message);

public record SendReactionRequest(string? Emoji, string? Text);

public record ScreenSettingsRequest(int PhotoSeconds, int LeaderboardSeconds, int ShopSeconds);

public record ScreenReactionDto(
    Guid Id,
    string? Emoji,
    string? Text,
    string AuthorName,
    DateTime CreatedAt);