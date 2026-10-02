using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.Api.Modules.Events;

/// <summary>
/// Подтверждение клеток бинго админом: только реально случившиеся события
/// приносят игрокам баллы и линии.
/// </summary>
public static class BingoEndpoints
{
    public static IEndpointRouteBuilder MapBingoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events/bingo")
            .WithTags("Bingo")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/{sessionId:guid}", async (
                Guid sessionId,
                BingoService bingo,
                CancellationToken ct) =>
            {
                object? state = await bingo.GetAdminStateAsync(sessionId, ct);

                return state is null
                    ? Results.NotFound(new { error = "Сессия бинго не найдена" })
                    : Results.Ok(state);
            });

        group.MapPost("/{sessionId:guid}/cells/{cellIndex:int}/confirm", async (
                Guid sessionId,
                int cellIndex,
                BingoService bingo,
                CancellationToken ct) =>
            {
                BingoConfirmOutcome outcome = await bingo.ConfirmCellAsync(sessionId, cellIndex, ct);

                return outcome.Success
                    ? Results.Ok(outcome.Data ?? new { success = true })
                    : Results.Conflict(new { error = outcome.Message });
            });

        return app;
    }
}