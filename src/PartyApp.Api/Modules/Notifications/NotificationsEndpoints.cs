using Microsoft.AspNetCore.SignalR;

using PartyApp.Api.Hubs;

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
            CancellationToken ct) =>
        {
            await hubContext.Clients.All.SendAsync("ReceiveBroadcast", request.Message, ct);
            return Results.Ok(new { success = true });
        });

        return app;
    }
}

public record BroadcastRequest(string Message);