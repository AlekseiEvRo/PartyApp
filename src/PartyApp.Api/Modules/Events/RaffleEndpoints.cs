using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.Api.Modules.Events;

/// <summary>Запуск розыгрыша лототрона админом.</summary>
public static class RaffleEndpoints
{
    public static IEndpointRouteBuilder MapRaffleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events/raffle")
            .WithTags("Raffle")
            .RequireAuthorization("AdminOnly");

        group.MapPost("/{sessionId:guid}/draw", async (
                Guid sessionId,
                RaffleService raffle,
                CancellationToken ct) =>
            {
                RaffleDrawOutcome outcome = await raffle.DrawAsync(sessionId, ct);

                return outcome.Success
                    ? Results.Ok(outcome.Data ?? new { success = true })
                    : Results.Conflict(new { error = outcome.Message });
            });

        return app;
    }
}