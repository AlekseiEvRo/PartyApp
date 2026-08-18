using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PartyApp.Api.Hubs;

[Authorize]
public class PartyHub : Hub
{
    private readonly ILogger<PartyHub> _logger;

    public PartyHub(ILogger<PartyHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst("sub")?.Value;
        var username = Context.User?.FindFirst("name")?.Value;

        _logger.LogInformation(
            "User {Username} ({UserId}) connected. ConnectionId: {ConnectionId}",
            username, userId, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "Connection {ConnectionId} disconnected",
            Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Тестовый метод: клиент отправляет сообщение, сервер рассылает его всем.
    /// </summary>
    public async Task SendMessage(string message)
    {
        var username = Context.User?.FindFirst("name")?.Value ?? "Unknown";
        await Clients.All.SendAsync("ReceiveMessage", username, message);
    }
}