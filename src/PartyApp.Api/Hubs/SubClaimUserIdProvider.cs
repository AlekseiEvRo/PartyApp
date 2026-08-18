using Microsoft.AspNetCore.SignalR;

namespace PartyApp.Api.Hubs;

public class SubClaimUserIdProvider: IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return connection.User?.FindFirst("sub")?.Value;
    }
}