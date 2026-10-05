using System.Security.Claims;

using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.Api.Modules.Events;

/// <summary>
/// Бинго в два этапа: админ закрывает приём предсказаний (1 этап), затем ставит
/// клеткам статусы «было» и «не было» (2 этап). Игрок может снять свой выбор,
/// пока приём открыт.
/// </summary>
public static class BingoEndpoints
{
    public static IEndpointRouteBuilder MapBingoEndpoints(this IEndpointRouteBuilder app)
    {
        var playerGroup = app.MapGroup("/api/events/bingo")
            .WithTags("Bingo")
            .RequireAuthorization();

        // Снять свой выбор можно только до блокировки приёма
        playerGroup.MapDelete("/{sessionId:guid}/marks/{cellIndex:int}", async (
                Guid sessionId,
                int cellIndex,
                ClaimsPrincipal user,
                BingoService bingo,
                CancellationToken ct) =>
            {
                var subClaim = user.FindFirst("sub")?.Value;
                if (subClaim is null || !Guid.TryParse(subClaim, out var playerId))
                    return Results.Unauthorized();

                BingoConfirmOutcome outcome = await bingo.RemoveMarkAsync(sessionId, playerId, cellIndex, ct);

                return outcome.Success
                    ? Results.Ok(outcome.Data ?? new { success = true })
                    : Results.Conflict(new { error = outcome.Message });
            });

        var adminGroup = app.MapGroup("/api/events/bingo")
            .WithTags("Bingo")
            .RequireAuthorization("AdminOnly");

        adminGroup.MapGet("/{sessionId:guid}", async (
                Guid sessionId,
                BingoService bingo,
                CancellationToken ct) =>
            {
                object? state = await bingo.GetAdminStateAsync(sessionId, ct);

                return state is null
                    ? Results.NotFound(new { error = "Сессия бинго не найдена" })
                    : Results.Ok(state);
            });

        // 1 этап → 2 этап: фиксируем выбор игроков
        adminGroup.MapPost("/{sessionId:guid}/lock", async (
                Guid sessionId,
                BingoService bingo,
                CancellationToken ct) =>
            {
                BingoConfirmOutcome outcome = await bingo.LockAsync(sessionId, ct);

                return outcome.Success
                    ? Results.Ok(outcome.Data ?? new { success = true })
                    : Results.Conflict(new { error = outcome.Message });
            });

        // Возврат к 1 этапу, пока по клеткам нет решений
        adminGroup.MapPost("/{sessionId:guid}/unlock", async (
                Guid sessionId,
                BingoService bingo,
                CancellationToken ct) =>
            {
                BingoConfirmOutcome outcome = await bingo.UnlockAsync(sessionId, ct);

                return outcome.Success
                    ? Results.Ok(outcome.Data ?? new { success = true })
                    : Results.Conflict(new { error = outcome.Message });
            });

        adminGroup.MapPost("/{sessionId:guid}/cells/{cellIndex:int}/confirm", async (
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

        adminGroup.MapPost("/{sessionId:guid}/cells/{cellIndex:int}/reject", async (
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
