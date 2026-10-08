using PartyApp.Domain.Entities;

namespace PartyApp.Api.Modules.Events.Services;

public interface IEventService
{
    Task<List<EventDefinition>> GetDefinitionsAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<List<AvailableEventDto>> GetAvailableEventsAsync(
        Guid? playerId = null,
        bool isAdmin = false,
        CancellationToken ct = default);
    Task<EventSession> StartEventAsync(Guid definitionId, Guid startedById, CancellationToken ct = default);
    Task FinishEventAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Завершает сессии, у которых истёк срок. Возвращает число завершённых.</summary>
    Task<int> CloseExpiredEventsAsync(CancellationToken ct = default);

    Task<SubmissionOutcome> SubmitToEventAsync(Guid sessionId, Guid playerId, string payloadJson, CancellationToken ct = default);
    Task<object?> GetEventDataAsync(
        Guid sessionId,
        Guid? playerId = null,
        bool isAdmin = false,
        CancellationToken ct = default);
}

public record AvailableEventDto(
    Guid SessionId,
    Guid DefinitionId,
    string Type,
    string DisplayName,
    string? Description,
    string Availability,
    DateTime StartedAt,
    DateTime? EndsAt);

public record SubmissionOutcome(
    bool Success,
    int PointsAwarded,
    string? Message,
    object? Data);