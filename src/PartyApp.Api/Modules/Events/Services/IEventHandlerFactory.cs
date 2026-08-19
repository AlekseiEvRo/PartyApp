using PartyApp.Api.Modules.Events.Handlers;

namespace PartyApp.Api.Modules.Events.Services;

public interface IEventHandlerFactory
{
    IEventHandler GetHandler(string eventType);
    bool HasHandler(string eventType);
}