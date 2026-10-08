using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events;

public static class EventsEndpoints
{
    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events").WithTags("Events");

        // === Для всех авторизованных игроков ===

        group.MapGet("/available", async (ClaimsPrincipal user, IEventService eventService, CancellationToken ct) =>
        {
            // Скрытые ивенты (шпионы) видит только их состав, админ — всё
            Guid? playerId = Guid.TryParse(user.FindFirst("sub")?.Value, out Guid parsed)
                ? parsed
                : null;
            bool isAdmin = user.IsInRole("Admin") || user.IsInRole("SuperAdmin");

            var events = await eventService.GetAvailableEventsAsync(playerId, isAdmin, ct);
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
        .RequireAuthorization()
        .RequireRateLimiting("submit");
        
        group.MapGet("/{sessionId:guid}/data", async (
                Guid sessionId,
                ClaimsPrincipal user,
                IEventService eventService,
                CancellationToken ct) =>
            {
                Guid? playerId = Guid.TryParse(user.FindFirst("sub")?.Value, out Guid parsed)
                    ? parsed
                    : null;
                bool isAdmin = user.IsInRole("Admin") || user.IsInRole("SuperAdmin");

                var data = await eventService.GetEventDataAsync(sessionId, playerId, isAdmin, ct);
                if (data is null)
                    return Results.NotFound(new { error = "Ивент не найден" });

                return Results.Ok(data);
            })
            .RequireAuthorization();

        // === Только для админов ===

        // Типы ивентов, для которых зарегистрирован обработчик
        group.MapGet("/types", (IEventHandlerFactory handlerFactory) =>
        {
            var types = handlerFactory.GetEventTypes()
                .OrderBy(t => t)
                .Select(t =>
                {
                    IEventHandler handler = handlerFactory.GetHandler(t);
                    return new
                    {
                        type = t,
                        defaultConfigJson = handler.DefaultConfigJson,
                        // Такие ивенты стартуют из своей админ-вкладки (например, «Шпионы»)
                        requiresCustomStart = handler.RequiresCustomStart
                    };
                })
                .ToList();

            return Results.Ok(types);
        })
        .RequireAuthorization("AdminOnly");

        group.MapGet("/definitions", async (
            bool? includeInactive,
            IEventService eventService,
            CancellationToken ct) =>
        {
            var definitions = await eventService.GetDefinitionsAsync(includeInactive == true, ct);
            return Results.Ok(definitions);
        })
        .RequireAuthorization("AdminOnly");

        // Создание нового определения ивента
        group.MapPost("/definitions", async (
            CreateEventDefinitionRequest request,
            ClaimsPrincipal user,
            AppDbContext db,
            IEventHandlerFactory handlerFactory,
            CancellationToken ct) =>
        {
            var subClaim = user.FindFirst("sub")?.Value;
            if (subClaim is null || !Guid.TryParse(subClaim, out var adminId))
                return Results.Unauthorized();

            var type = request.Type?.Trim() ?? string.Empty;
            if (!handlerFactory.HasHandler(type))
                return Results.BadRequest(new { error = $"Неизвестный тип ивента: {type}" });

            var validationError = ValidateDefinitionFields(request.DisplayName, request.ConfigJson, out var displayName, out var configJson);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            if (request.DurationMinutes is < 1 or > 1440)
                return Results.BadRequest(new { error = "Длительность — от 1 до 1440 минут" });

            var definition = new EventDefinition
            {
                Type = type,
                DisplayName = displayName,
                Description = NormalizeDescription(request.Description),
                ConfigJson = configJson,
                Availability = AvailabilityMode.Manual,
                DurationMinutes = request.DurationMinutes,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedById = adminId
            };

            db.EventDefinitions.Add(definition);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/events/definitions/{definition.Id}", definition);
        })
        .RequireAuthorization("AdminOnly");

        // Редактирование определения. Type не меняется после создания
        group.MapPut("/definitions/{definitionId:guid}", async (
            Guid definitionId,
            UpdateEventDefinitionRequest request,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var definition = await db.EventDefinitions.FindAsync(new object[] { definitionId }, ct);
            if (definition is null)
                return Results.NotFound(new { error = "Ивент не найден" });

            var validationError = ValidateDefinitionFields(request.DisplayName, request.ConfigJson, out var displayName, out var configJson);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            if (request.DurationMinutes is < 1 or > 1440)
                return Results.BadRequest(new { error = "Длительность — от 1 до 1440 минут" });

            definition.DisplayName = displayName;
            definition.Description = NormalizeDescription(request.Description);
            definition.ConfigJson = configJson;
            definition.DurationMinutes = request.DurationMinutes;
            definition.IsActive = request.IsActive;

            await db.SaveChangesAsync(ct);

            return Results.Ok(definition);
        })
        .RequireAuthorization("AdminOnly");

        // Системные определения (созданные сидом) удалять нельзя, только деактивировать.
        // Определения с сессиями тоже нельзя — иначе каскад снесёт историю
        group.MapDelete("/definitions/{definitionId:guid}", async (
            Guid definitionId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var definition = await db.EventDefinitions.FindAsync(new object[] { definitionId }, ct);
            if (definition is null)
                return Results.NotFound(new { error = "Ивент не найден" });

            if (definition.CreatedById is null)
                return Results.Conflict(new { error = "Системный ивент нельзя удалить — только деактивировать" });

            var sessionCount = await db.EventSessions.CountAsync(s => s.DefinitionId == definitionId, ct);
            if (sessionCount > 0)
                return Results.Conflict(new { error = $"Нельзя удалить: у ивента уже есть сессии ({sessionCount}). Деактивируйте его вместо удаления" });

            db.EventDefinitions.Remove(definition);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { success = true });
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

            try
            {
                var session = await eventService.StartEventAsync(definitionId, adminId, ct);

                return Results.Ok(new
                {
                    sessionId = session.Id,
                    state = session.State.ToString(),
                    startedAt = session.StartedAt,
                    endsAt = session.EndsAt
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .RequireAuthorization("AdminOnly");

        group.MapPost("/{sessionId:guid}/finish", async (
            Guid sessionId,
            IEventService eventService,
            CancellationToken ct) =>
        {
            try
            {
                await eventService.FinishEventAsync(sessionId, ct);
                return Results.Ok(new { success = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .RequireAuthorization("AdminOnly");

        return app;
    }

    private static string? ValidateDefinitionFields(
        string? displayName,
        string? configJson,
        out string normalizedDisplayName,
        out string normalizedConfigJson)
    {
        normalizedDisplayName = displayName?.Trim() ?? string.Empty;
        normalizedConfigJson = string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson;

        if (normalizedDisplayName.Length == 0)
            return "Название не может быть пустым";

        if (normalizedDisplayName.Length > 100)
            return "Название не должно быть длиннее 100 символов";

        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                using var document = JsonDocument.Parse(configJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return "ConfigJson должен быть JSON-объектом";
            }
            catch (JsonException)
            {
                return "ConfigJson не является валидным JSON";
            }
        }

        return null;
    }

    private static string? NormalizeDescription(string? description)
    {
        return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }
}

public record SubmitRequest(string? PayloadJson);
public record CreateEventDefinitionRequest(
    string? Type,
    string? DisplayName,
    string? Description,
    string? ConfigJson,
    int? DurationMinutes = null);
public record UpdateEventDefinitionRequest(
    string? DisplayName,
    string? Description,
    string? ConfigJson,
    bool IsActive = true,
    int? DurationMinutes = null);