using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Screen;

/// <summary>
/// Управление большим экраном: админ переключает режимы, состояние уходит
/// всем подключённым клиентам событием ScreenUpdated.
/// </summary>
public static class ScreenEndpoints
{
    private const int MaxMessageLength = 200;

    public static IEndpointRouteBuilder MapScreenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/screen")
            .WithTags("Screen")
            .RequireAuthorization();

        group.MapGet("/state", (ScreenService screen) => Results.Ok(screen.GetState()));

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
            .RequireAuthorization("AdminOnly");

        return app;
    }
}

public record SetScreenStateRequest(string? Mode, Guid? SessionId, string? Message);