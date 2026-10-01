using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

using PartyApp.Domain.Entities;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Realtime;

/// <summary>
/// Проверка реального времени: настоящий SignalR-клиент подключается
/// к /hubs/party через TestServer и получает рассылки приложения.
/// </summary>
public class SignalRTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public SignalRTests()
    {
        _api = new PartyAppApi(_factory);
        _ = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HubConnection BuildConnection(TestUser? user)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/party"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (user is not null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(user.Token);
            })
            .Build();
    }

    private async Task<HubConnection> ConnectAsync(TestUser user)
    {
        HubConnection connection = BuildConnection(user);
        await connection.StartAsync();
        return connection;
    }

    private static async Task<JsonElement> WaitForAsync(HubConnection connection, string method)
    {
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = connection.On<JsonElement>(method, payload => completion.TrySetResult(payload));

        Task finished = await Task.WhenAny(completion.Task, Task.Delay(Timeout));
        if (finished != completion.Task)
            throw new TimeoutException($"Не дождались сообщения «{method}» от хаба за {Timeout}");

        return await completion.Task;
    }

    private static async Task<string> WaitForStringAsync(HubConnection connection, string method)
    {
        TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = connection.On<string>(method, payload => completion.TrySetResult(payload));

        Task finished = await Task.WhenAny(completion.Task, Task.Delay(Timeout));
        if (finished != completion.Task)
            throw new TimeoutException($"Не дождались сообщения «{method}» от хаба за {Timeout}");

        return await completion.Task;
    }

    [Fact]
    public async Task Hub_WithoutToken_RejectsConnection()
    {
        await using HubConnection connection = BuildConnection(user: null);

        Func<Task> act = () => connection.StartAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task BalanceUpdated_IsDeliveredToUser()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        await using HubConnection connection = await ConnectAsync(player);
        Task<JsonElement> message = WaitForAsync(connection, "BalanceUpdated");

        _api.Authorize(admin);
        HttpResponseMessage grant = await _api.Client.PostAsJsonAsync(
            "/api/admin/grant-points",
            new { playerId = player.Id, amount = 7, reason = "За тост" });
        grant.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("balance").GetInt32().Should().Be(107);
    }

    [Fact]
    public async Task EventStarted_IsBroadcastToAllPlayers()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();
        EventDefinition definition = await _factory.SeedDefinitionAsync(
            "quiz", displayName: "Сигнальный квиз", isActive: true);

        await using HubConnection connection = await ConnectAsync(player);
        Task<JsonElement> message = WaitForAsync(connection, "EventStarted");

        _api.Authorize(admin);
        HttpResponseMessage start = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        start.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(start)).GetProperty("sessionId").GetGuid();

        JsonElement payload = await message;
        payload.GetProperty("sessionId").GetGuid().Should().Be(sessionId);
        payload.GetProperty("type").GetString().Should().Be("quiz");
        payload.GetProperty("displayName").GetString().Should().Be("Сигнальный квиз");
    }

    [Fact]
    public async Task ReceiveBroadcast_DeliversHostMessageToPlayers()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        await using HubConnection connection = await ConnectAsync(player);
        Task<string> message = WaitForStringAsync(connection, "ReceiveBroadcast");

        _api.Authorize(admin);
        HttpResponseMessage broadcast = await _api.Client.PostAsJsonAsync(
            "/api/notifications/broadcast", new { message = "Все танцуем!" });
        broadcast.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        (await message).Should().Be("Все танцуем!");
    }

    [Fact]
    public async Task SpyGameRoleAssigned_IsDeliveredToParticipant()
    {
        TestUser admin = await _api.CreateAdminAsync();
        List<TestUser> players = new();
        for (int i = 0; i < 4; i++)
        {
            PartyAppApi api = new(_factory);
            TestUser user = await api.RegisterAsync();
            players.Add(user);
        }

        await using HubConnection connection = await ConnectAsync(players[0]);
        Task<JsonElement> message = WaitForAsync(connection, "SpyGameRoleAssigned");

        _api.Authorize(admin);
        HttpResponseMessage start = await _api.Client.PostAsJsonAsync(
            "/api/spygame/start", new { playerIds = players.Select(p => p.Id).ToList() });
        start.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("role").GetString().Should().BeOneOf("Spy", "Townsfolk");
        payload.GetProperty("allPlayers").GetArrayLength().Should().Be(3);
        payload.GetProperty("sessionId").GetGuid().Should().NotBe(Guid.Empty);
    }
}