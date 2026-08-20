using System.Security.Claims;

namespace PartyApp.Api.Modules.SpyGame;

public static class SpyGameEndpoints
{
    public static IEndpointRouteBuilder MapSpyGameEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spygame").WithTags("SpyGame");

        // === Админские (запуск/завершение) ===

        group.MapPost("/start", async (
                StartGameRequest request,
                SpyGameService service,
                CancellationToken ct) =>
            {
                var state = await service.StartGameAsync(request.PlayerIds, ct);
                return Results.Ok(new
                {
                    sessionId = state.SessionId,
                    playersCount = state.Players.Count,
                    secretWord = state.SecretWord // админ видит слово для контроля
                });
            })
            .RequireAuthorization("AdminOnly");

        group.MapPost("/finish", async (
                SpyGameService service,
                CancellationToken ct) =>
            {
                var result = await service.FinishGameAsync(ct);
                return result is null
                    ? Results.BadRequest(new { error = "Игра не запущена" })
                    : Results.Ok(result);
            })
            .RequireAuthorization("AdminOnly");

        group.MapGet("/state", (SpyGameService service) =>
            {
                var state = service.GetCurrentState();
                return Results.Ok(new
                {
                    phase = state.Phase.ToString(),
                    sessionId = state.SessionId,
                    startedAt = state.StartedAt,
                    finishedAt = state.FinishedAt,
                    winner = state.Winner,
                    players = state.Players.Select(p => new
                    {
                        p.UserId, p.DisplayName, Role = p.Role.ToString(), p.HasAccused
                    }),
                    secretWord = state.SecretWord,
                    townWinnerName = state.TownWinnerName
                });
            })
            .RequireAuthorization("AdminOnly");

        // === Игровые (для участников) ===

        group.MapGet("/my-role", async (
                ClaimsPrincipal user,
                SpyGameService service,
                CancellationToken ct) =>
            {
                var subClaim = user.FindFirst("sub")?.Value;
                if (subClaim is null || !Guid.TryParse(subClaim, out var userId))
                    return Results.Unauthorized();

                var state = service.GetCurrentState();
                if (state.Phase != SpyGamePhase.Playing)
                    return Results.NotFound(new { error = "Игра не запущена" });

                var role = state.Players.FirstOrDefault(p => p.UserId == userId);
                if (role is null)
                    return Results.NotFound(new { error = "Ты не участвуешь в игре" });

                return Results.Ok(new
                {
                    sessionId = state.SessionId,
                    role = role.Role.ToString(),
                    isGuesser = role.IsGuesser,
                    partnerName = role.PartnerName,
                    secretWord = role.SecretWord,
                    allPlayers = state.Players
                        .Where(p => p.UserId != userId)
                        .Select(p => new { userId = p.UserId, displayName = p.DisplayName })
                });
            })
            .RequireAuthorization();

        group.MapPost("/submit-word", async (
                ClaimsPrincipal user,
                SubmitWordRequest request,
                SpyGameService service,
                CancellationToken ct) =>
            {
                var subClaim = user.FindFirst("sub")?.Value;
                if (subClaim is null || !Guid.TryParse(subClaim, out var userId))
                    return Results.Unauthorized();

                var result = await service.SubmitWordAsync(userId, request.Word ?? string.Empty, ct);

                return result.Success
                    ? Results.Ok(new { result.Message, result.PointsAwarded, data = result.Data })
                    : Results.BadRequest(new { error = result.Message });
            })
            .RequireAuthorization();

        group.MapPost("/accuse", async (
                ClaimsPrincipal user,
                AccuseRequest request,
                SpyGameService service,
                CancellationToken ct) =>
            {
                var subClaim = user.FindFirst("sub")?.Value;
                if (subClaim is null || !Guid.TryParse(subClaim, out var userId))
                    return Results.Unauthorized();

                var result = await service.AccuseAsync(userId, request.AccusedPlayerId, ct);

                return result.Success
                    ? Results.Ok(new { result.Message, result.PointsAwarded, data = result.Data })
                    : Results.BadRequest(new { error = result.Message });
            })
            .RequireAuthorization();

        return app;
    }
}

public record StartGameRequest(List<Guid> PlayerIds);

public record SubmitWordRequest(string? Word);

public record AccuseRequest(Guid AccusedPlayerId);