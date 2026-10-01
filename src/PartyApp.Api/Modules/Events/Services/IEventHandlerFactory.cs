using PartyApp.Api.Modules.Events.Handlers;

namespace PartyApp.Api.Modules.Events.Services;

public interface IEventHandlerFactory
{
    IReadOnlyCollection<string> GetEventTypes();
    IEventHandler GetHandler(string eventType);
    bool HasHandler(string eventType);
}