using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.Api.Modules.Events;

public static class EventsEndpoints
{
    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events").WithTags("Events");

        // === Для всех авторизованных игроков ===

        group.MapGet("/available", async (IEventService eventService, CancellationToken ct) =>
        {
            var events = await eventService.GetAvailableEventsAsync(ct);
            return Results.Ok(events);
        })
        .RequireAuthorization();

        group.MapPost("/{sessionId:guid}/submit", async (
            Guid sessionId,
            ClaimsPrincipal user,
            SubmitRequest request,
            IEventService eventService,
            CancellationToken ct) =>
        {
            var subClaim = user.FindFirst("sub")?.Value;
            if (subClaim is null || !Guid.TryParse(subClaim, out var playerId))
                return Results.Unauthorized();

            var outcome = await eventService.SubmitToEventAsync(sessionId, playerId, request.PayloadJson ?? "{}", ct);

            if (!outcome.Success)
                return Results.Conflict(new { error = outcome.Message, data = outcome.Data });

            return Results.Ok(new
            {
                message = outcome.Message,
                pointsAwarded = outcome.PointsAwarded,
                data = outcome.Data
            });
        })
        .RequireAuthorization();
        
        group.MapGet("/{sessionId:guid}/data", async (
                Guid sessionId,
                IEventService eventService,
                CancellationToken ct) =>
            {
                var data = await eventService.GetEventDataAsync(sessionId, ct);
                if (data is null)
                    return Results.NotFound(new { error = "Ивент не найден" });

                return Results.Ok(data);
            })
            .RequireAuthorization();

        // === Только для админов ===

        group.MapGet("/definitions", async (IEventService eventService, CancellationToken ct) =>
        {
            var definitions = await eventService.GetDefinitionsAsync(ct);
            return Results.Ok(definitions);
        })
        .RequireAuthorization("AdminOnly");

        group.MapPost("/{definitionId:guid}/start", async (
            Guid definitionId,
            ClaimsPrincipal user,
            IEventService eventService,
            CancellationToken ct) =>
        {
            var subClaim = user.FindFirst("sub")?.Value;
            if (subClaim is null || !Guid.TryParse(subClaim, out var adminId))
                return Results.Unauthorized();

            var session = await eventService.StartEventAsync(definitionId, adminId, ct);

            return Results.Ok(new
            {
                sessionId = session.Id,
                state = session.State.ToString(),
                startedAt = session.StartedAt
            });
        })
        .RequireAuthorization("AdminOnly");

        group.MapPost("/{sessionId:guid}/finish", async (
            Guid sessionId,
            IEventService eventService,
            CancellationToken ct) =>
        {
            await eventService.FinishEventAsync(sessionId, ct);
            return Results.Ok(new { success = true });
        })
        .RequireAuthorization("AdminOnly");

        return app;
    }
}

public record SubmitRequest(string? PayloadJson);