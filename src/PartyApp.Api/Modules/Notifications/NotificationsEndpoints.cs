using Microsoft.AspNetCore.SignalR;

using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Push;

namespace PartyApp.Api.Modules.Notifications;

public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications")
            .WithTags("Notifications")
            .RequireAuthorization("AdminOnly");

        group.MapPost("/broadcast", async (
            BroadcastRequest request,
            IHubContext<PartyHub> hubContext,
            IPushNotificationService push,
            CancellationToken ct) =>
        {
            await hubContext.Clients.All.SendAsync("ReceiveBroadcast", request.Message, ct);

            // Push тем, у кого приложение закрыто
            await push.SendToAllAsync(
                new PushMessage(
                    Title: "📢 Сообщение от ведущего",
                    Body: request.Message,
                    Url: "/",
                    Tag: "broadcast"),
                ct: ct);

            return Results.Ok(new { success = true });
        });

        return app;
    }
}

public record BroadcastRequest(string Message);