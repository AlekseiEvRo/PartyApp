using System.Security.Claims;

using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.Api.Modules.Events;

/// <summary>
/// Игра «Шпионы»: запуск с выбором участников и состояние для админ-вкладки.
/// Игроки ходят через общие эндпоинты submit/data — их обрабатывает SpyFallHandler.
/// </summary>
public static class SpyFallEndpoints
{
    public static IEndpointRouteBuilder MapSpyFallEndpoints(this IEndpointRouteBuilder app)
    {
        var adminGroup = app.MapGroup("/api/events/spyfall")
            .WithTags("SpyFall")
            .RequireAuthorization("AdminOnly");

        // Запуск только отсюда: обычная кнопка старта не знает состав участников
        adminGroup.MapPost("/start", async (
                StartSpyFallRequest request,
                ClaimsPrincipal user,
                SpyFallService spyFall,
                CancellationToken ct) =>
            {
                var subClaim = user.FindFirst("sub")?.Value;
                if (subClaim is null || !Guid.TryParse(subClaim, out Guid adminId))
                    return Results.Unauthorized();

                SpyFallOutcome outcome = await spyFall.StartAsync(
                    request.DefinitionId,
                    adminId,
                    request.PlayerIds ?? new List<Guid>(),
                    ct);

                return outcome.Success
                    ? Results.Ok(outcome.Data ?? new { success = true })
                    : Results.BadRequest(new { error = outcome.Message });
            });

        // Последняя игра (активная или завершённая) — для вкладки «Шпионы»
        adminGroup.MapGet("/current", async (SpyFallService spyFall, CancellationToken ct) =>
        {
            object? state = await spyFall.GetCurrentAsync(ct);
            // Пустое тело клиенту не подходит — нужен явный JSON null
            return state is null
                ? Results.Text("null", "application/json")
                : Results.Json(state);
        });

        adminGroup.MapGet("/{sessionId:guid}", async (
                Guid sessionId,
                SpyFallService spyFall,
                CancellationToken ct) =>
            {
                object? state = await spyFall.GetAdminStateAsync(sessionId, ct);

                return state is null
                    ? Results.NotFound(new { error = "Сессия «Шпионов» не найдена" })
                    : Results.Ok(state);
            });

        return app;
    }
}

public record StartSpyFallRequest(Guid DefinitionId, List<Guid>? PlayerIds);