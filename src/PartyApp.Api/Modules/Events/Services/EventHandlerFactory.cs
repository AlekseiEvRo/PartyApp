using PartyApp.Api.Modules.Events.Handlers;

namespace PartyApp.Api.Modules.Events.Services;

public class EventHandlerFactory : IEventHandlerFactory
{
    private readonly Dictionary<string, IEventHandler> _handlers;
    private readonly ILogger<EventHandlerFactory> _logger;

    public EventHandlerFactory(IEnumerable<IEventHandler> handlers, ILogger<EventHandlerFactory> logger)
    {
        _logger = logger;
        _handlers = new Dictionary<string, IEventHandler>(StringComparer.OrdinalIgnoreCase);

        foreach (var handler in handlers)
        {
            if (_handlers.ContainsKey(handler.EventType))
            {
                _logger.LogWarning(
                    "Duplicate event handler for type {EventType}. Skipping {HandlerType}",
                    handler.EventType, handler.GetType().Name);
                continue;
            }

            _handlers[handler.EventType] = handler;
            _logger.LogInformation(
                "Registered event handler: {EventType} → {HandlerType}",
                handler.EventType, handler.GetType().Name);
        }
    }

    public IEventHandler GetHandler(string eventType)
    {
        if (_handlers.TryGetValue(eventType, out var handler))
            return handler;

        throw new InvalidOperationException($"No event handler registered for type: {eventType}");
    }

    public bool HasHandler(string eventType) => _handlers.ContainsKey(eventType);
}