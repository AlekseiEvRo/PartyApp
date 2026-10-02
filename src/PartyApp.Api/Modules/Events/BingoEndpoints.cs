using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.Api.Modules.Events;

/// <summary>
/// Решения админа по клеткам бинго: подтверждение («было») и отклонение («не было»).
/// Только подтверждённые клетки приносят баллы; отклонение освобождает слоты предсказаний.
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

        group.MapPost("/{sessionId:guid}/cells/{cellIndex:int}/reject", async (
                Guid sessionId,
                int cellIndex,
                BingoService bingo,
                CancellationToken ct) =>
            {
                BingoConfirmOutcome outcome = await bingo.RejectCellAsync(sessionId, cellIndex, ct);

                return outcome.Success
                    ? Results.Ok(outcome.Data ?? new { success = true })
                    : Results.Conflict(new { error = outcome.Message });
            });

        return app;
    }
}