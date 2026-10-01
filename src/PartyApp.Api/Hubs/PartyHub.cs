using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PartyApp.Api.Hubs;

[Authorize]
public class PartyHub : Hub
{
    /// <summary>Группа SignalR для админов: туда уходят события модерации.</summary>
    public const string AdminsGroup = "admins";

    private readonly ILogger<PartyHub> _logger;

    public PartyHub(ILogger<PartyHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst("sub")?.Value;
        var username = Context.User?.FindFirst("name")?.Value;

        // Админ подписывается на уведомления о новом контенте на модерации
        if (Context.User?.IsInRole("Admin") == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AdminsGroup);
        }

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