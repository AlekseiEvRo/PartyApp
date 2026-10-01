using System.Text.Json;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Push;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Services;

public class EventService : IEventService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventHandlerFactory _handlerFactory;
    private readonly IHubContext<PartyHub> _hubContext;
    private readonly IPushNotificationService _push;
    private readonly ILogger<EventService> _logger;

    public EventService(
        IServiceScopeFactory scopeFactory,
        IEventHandlerFactory handlerFactory,
        IHubContext<PartyHub> hubContext,
        IPushNotificationService push,
        ILogger<EventService> logger)
    {
        _scopeFactory = scopeFactory;
        _handlerFactory = handlerFactory;
        _hubContext = hubContext;
        _push = push;
        _logger = logger;
    }

    public async Task<List<EventDefinition>> GetDefinitionsAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        IQueryable<EventDefinition> query = db.EventDefinitions;
        if (!includeInactive)
            query = query.Where(d => d.IsActive);

        return await query
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<AvailableEventDto>> GetAvailableEventsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Возвращаем все активные сессии
        var activeSessions = await db.EventSessions
            .Include(s => s.Definition)
            .Where(s => s.State == EventSessionState.Active && s.Definition.IsActive)
            .ToListAsync(ct);

        return activeSessions
            .Select(s => new AvailableEventDto(
                SessionId: s.Id,
                DefinitionId: s.DefinitionId,
                Type: s.Definition.Type,
                DisplayName: s.Definition.DisplayName,
                Description: s.Definition.Description,
                Availability: s.Definition.Availability.ToString(),
                StartedAt: s.StartedAt))
            .ToList();
    }

    public async Task<EventSession> StartEventAsync(Guid definitionId, Guid startedById, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var definition = await db.EventDefinitions.FindAsync(new object[] { definitionId }, ct);
        if (definition is null)
            throw new InvalidOperationException($"Event definition {definitionId} not found");

        if (!definition.IsActive)
            throw new InvalidOperationException($"Event definition {definitionId} is not active");

        if (!_handlerFactory.HasHandler(definition.Type))
            throw new InvalidOperationException($"No handler registered for event type: {definition.Type}");

        var session = new EventSession
        {
            DefinitionId = definitionId,
            StartedById = startedById,
            State = EventSessionState.Active,
            StartedAt = DateTime.UtcNow
        };

        db.EventSessions.Add(session);
        await db.SaveChangesAsync(ct);

        // Вызываем обработчик для инициализации
        var handler = _handlerFactory.GetHandler(definition.Type);
        await handler.OnSessionStartedAsync(session, definition, ct);

        // Рассылаем всем клиентам событие старта
        await _hubContext.Clients.All.SendAsync("EventStarted", new
        {
            sessionId = session.Id,
            definitionId = definition.Id,
            type = definition.Type,
            displayName = definition.DisplayName,
            description = definition.Description,
            availability = definition.Availability.ToString(),
            startedAt = session.StartedAt
        }, ct);

        // Push тем, у кого приложение закрыто
        await _push.SendToAllAsync(
            new PushMessage(
                Title: $"🎉 {definition.DisplayName}",
                Body: definition.Description ?? "Скорее участвуй!",
                Url: "/",
                Tag: $"event-{session.Id}"),
            ct: ct);

        _logger.LogInformation(
            "Event started: {DisplayName} (type={Type}, session={SessionId})",
            definition.DisplayName, definition.Type, session.Id);

        return session;
    }

    public async Task FinishEventAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = await db.EventSessions
            .Include(s => s.Definition)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
            throw new InvalidOperationException($"Event session {sessionId} not found");

        if (session.State != EventSessionState.Active)
            throw new InvalidOperationException($"Event session {sessionId} is not active");

        session.State = EventSessionState.Finished;
        session.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Вызываем обработчик для завершения
        var handler = _handlerFactory.GetHandler(session.Definition.Type);
        await handler.OnSessionFinishedAsync(session, session.Definition, ct);

        // Рассылаем всем клиентам событие завершения
        await _hubContext.Clients.All.SendAsync("EventFinished", new
        {
            sessionId = session.Id
        }, ct);

        _logger.LogInformation(
            "Event finished: {DisplayName} (session={SessionId})",
            session.Definition.DisplayName, session.Id);
    }

    public async Task<SubmissionOutcome> SubmitToEventAsync(Guid sessionId, Guid playerId, string payloadJson, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = await db.EventSessions
            .Include(s => s.Definition)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
            return new SubmissionOutcome(false, 0, "Ивент не найден", null);

        if (session.State != EventSessionState.Active)
            return new SubmissionOutcome(false, 0, "Ивент не активен", null);

        var handler = _handlerFactory.GetHandler(session.Definition.Type);

        var result = await handler.HandleSubmissionAsync(session, session.Definition, playerId, payloadJson, ct);

        // Сохраняем сабмит в БД
        var submission = new PlayerSubmission
        {
            SessionId = sessionId,
            PlayerId = playerId,
            PayloadJson = payloadJson,
            Score = result.PointsAwarded,
            SubmittedAt = DateTime.UtcNow
        };

        db.PlayerSubmissions.Add(submission);
        await db.SaveChangesAsync(ct);

        return new SubmissionOutcome(result.Success, result.PointsAwarded, result.Message, result.Data);
    }

    public async Task<object?> GetEventDataAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = await db.EventSessions
            .Include(s => s.Definition)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
            return null;

        // «Живые» данные обработчика (например, кто сейчас говорит тост)
        object? live = null;
        if (_handlerFactory.HasHandler(session.Definition.Type))
        {
            var handler = _handlerFactory.GetHandler(session.Definition.Type);
            live = await handler.GetLiveDataAsync(session, session.Definition, ct);
        }

        // Возвращаем конфиг как объект
        try
        {
            var config = JsonSerializer.Deserialize<JsonElement>(session.Definition.ConfigJson, EventJsonOptions.Default);
            return new
            {
                sessionId = session.Id,
                type = session.Definition.Type,
                displayName = session.Definition.DisplayName,
                config,
                live
            };
        }
        catch
        {
            return new
            {
                sessionId = session.Id,
                type = session.Definition.Type,
                displayName = session.Definition.DisplayName,
                config = new { },
                live
            };
        }
    }
}