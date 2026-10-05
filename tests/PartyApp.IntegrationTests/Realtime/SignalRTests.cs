using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

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

    private readonly PartyAppFactory _factory = new()
    {
        // Фото уходят на модерацию: проверяем уведомления админам
        ConfigureOverrides = settings => settings["Photos:RequireModeration"] = "true"
    };
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
        start.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("role").GetString().Should().BeOneOf("Spy", "Townsfolk");
        payload.GetProperty("allPlayers").GetArrayLength().Should().Be(3);
        payload.GetProperty("sessionId").GetGuid().Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task PhotoRemoved_IsBroadcastToAllPlayers()
    {
        TestUser author = await _api.RegisterAsync();
        await using HubConnection connection = await ConnectAsync(author);
        Task<JsonElement> removedMessage = WaitForAsync(connection, "PhotoRemoved");

        _api.Authorize(author);
        ByteArrayContent file = new(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        MultipartFormDataContent form = new();
        form.Add(file, "file", "photo.jpg");

        HttpResponseMessage upload = await _api.Client.PostAsync("/api/photos", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid photoId = (await PartyAppApi.ReadJsonAsync(upload)).GetProperty("id").GetGuid();

        HttpResponseMessage delete = await _api.Client.DeleteAsync($"/api/photos/{photoId}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement payload = await removedMessage;
        payload.GetProperty("photoId").GetGuid().Should().Be(photoId);
    }

    [Fact]
    public async Task WishRemoved_IsBroadcastToAllPlayers()
    {
        TestUser author = await _api.RegisterAsync();
        await using HubConnection connection = await ConnectAsync(author);
        Task<JsonElement> removedMessage = WaitForAsync(connection, "WishRemoved");

        _api.Authorize(author);
        HttpResponseMessage submit = await _api.Client.PostAsJsonAsync(
            "/api/wishes", new { text = "Пожелание под удаление" });
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid wishId = (await PartyAppApi.ReadJsonAsync(submit)).GetProperty("id").GetGuid();

        HttpResponseMessage delete = await _api.Client.DeleteAsync($"/api/wishes/{wishId}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement payload = await removedMessage;
        payload.GetProperty("wishId").GetGuid().Should().Be(wishId);
    }

    [Fact]
    public async Task WishRemoved_IsBroadcastWhenApprovedWishIsRejected()
    {
        TestUser author = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();
        await using HubConnection connection = await ConnectAsync(author);

        _api.Authorize(author);
        HttpResponseMessage submit = await _api.Client.PostAsJsonAsync(
            "/api/wishes", new { text = "Сначала одобрили, потом передумали" });
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid wishId = (await PartyAppApi.ReadJsonAsync(submit)).GetProperty("id").GetGuid();

        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/wishes/{wishId}/approve", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        Task<JsonElement> removedMessage = WaitForAsync(connection, "WishRemoved");

        (await _api.Client.PostAsync($"/api/wishes/{wishId}/reject", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        JsonElement payload = await removedMessage;
        payload.GetProperty("wishId").GetGuid().Should().Be(wishId);
    }

    [Fact]
    public async Task ModerationPending_ForPhoto_IsDeliveredToAdminWithPush()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        await using HubConnection connection = await ConnectAsync(admin);

        Task<JsonElement> message = WaitForAsync(connection, "ModerationPending");

        _api.Authorize(player);
        ByteArrayContent file = new(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        MultipartFormDataContent form = new();
        form.Add(file, "file", "photo.jpg");
        HttpResponseMessage upload = await _api.Client.PostAsync("/api/photos", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid photoId = (await PartyAppApi.ReadJsonAsync(upload)).GetProperty("id").GetGuid();

        JsonElement payload = await message;
        payload.GetProperty("kind").GetString().Should().Be("photo");
        payload.GetProperty("itemId").GetGuid().Should().Be(photoId);
        payload.GetProperty("authorName").GetString().Should().Be(player.DisplayName);

        // Админам ушёл push
        _factory.Push.Calls.Should().Contain(call =>
            call.UserIds != null
            && call.UserIds.Contains(admin.Id)
            && call.Message.Tag == "moderation-photo");
    }

    [Fact]
    public async Task ModerationPending_ForWish_IsDeliveredToAdminWithPreview()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        await using HubConnection connection = await ConnectAsync(admin);

        Task<JsonElement> message = WaitForAsync(connection, "ModerationPending");

        _api.Authorize(player);
        HttpResponseMessage submit = await _api.Client.PostAsJsonAsync(
            "/api/wishes", new { text = "Проверь моё пожелание" });
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid wishId = (await PartyAppApi.ReadJsonAsync(submit)).GetProperty("id").GetGuid();

        JsonElement payload = await message;
        payload.GetProperty("kind").GetString().Should().Be("wish");
        payload.GetProperty("itemId").GetGuid().Should().Be(wishId);
        payload.GetProperty("preview").GetString().Should().Be("Проверь моё пожелание");

        // На отзыв админам тоже уходит push, отдельным тегом от фото
        _factory.Push.Calls.Should().Contain(call =>
            call.UserIds != null
            && call.UserIds.Contains(admin.Id)
            && call.Message.Tag == "moderation-wish"
            && call.Message.Title.Contains("отзыв"));
    }

    [Fact]
    public async Task ModerationPending_IsNotDeliveredToPlayers()
    {
        TestUser player = await _api.RegisterAsync();
        await using HubConnection connection = await ConnectAsync(player);

        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = connection.On<JsonElement>(
            "ModerationPending", payload => completion.TrySetResult(payload));

        _api.Authorize(player);
        HttpResponseMessage submit = await _api.Client.PostAsJsonAsync(
            "/api/wishes", new { text = "Событие не для игроков" });
        submit.StatusCode.Should().Be(HttpStatusCode.Created);

        Task finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(1)));

        finished.Should().NotBe(completion.Task, "игрок не должен получать события модерации");
    }

    [Fact]
    public async Task ScreenUpdated_IsBroadcastToAllClients()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();
        await using HubConnection connection = await ConnectAsync(player);

        Task<JsonElement> message = WaitForAsync(connection, "ScreenUpdated");

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "leaderboard" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("mode").GetString().Should().Be("leaderboard");
        payload.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ScreenConfetti_IsBroadcastToAllClients()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();
        await using HubConnection connection = await ConnectAsync(player);

        Task<JsonElement> message = WaitForAsync(connection, "ScreenConfetti");

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsync("/api/screen/confetti", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ScreenReaction_IsBroadcastToAllClients()
    {
        TestUser author = await _api.RegisterAsync();
        TestUser other = await _api.RegisterAsync();
        await using HubConnection connection = await ConnectAsync(other);

        Task<JsonElement> message = WaitForAsync(connection, "ScreenReaction");

        _api.Authorize(author);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/screen/reactions", new { emoji = "🔥", text = "Давай!" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("emoji").GetString().Should().Be("🔥");
        payload.GetProperty("text").GetString().Should().Be("Давай!");
        payload.GetProperty("authorName").GetString().Should().Be(author.DisplayName);
    }

    [Fact]
    public async Task ShopUpdated_IsBroadcastWhenAdminCreatesItem()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();
        await using HubConnection connection = await ConnectAsync(player);

        Task<JsonElement> message = WaitForAsync(connection, "ShopUpdated");

        _api.Authorize(admin);
        HttpResponseMessage created = await _api.Client.PostAsJsonAsync(
            "/api/shop/items",
            new { name = "Коктейль", description = (string?)null, price = 30, stock = (int?)null, isActive = true });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        JsonElement payload = await message;
        payload.GetProperty("itemId").GetGuid().Should()
            .Be((await PartyAppApi.ReadJsonAsync(created)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task PurchaseUpdated_IsBroadcastWhenPrizeIsFulfilled()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(admin);
        HttpResponseMessage created = await _api.Client.PostAsJsonAsync(
            "/api/shop/items",
            new { name = "Шампанское", description = (string?)null, price = 10, stock = (int?)null, isActive = true });
        Guid itemId = (await PartyAppApi.ReadJsonAsync(created)).GetProperty("id").GetGuid();

        _api.Authorize(player);
        await using HubConnection connection = await ConnectAsync(player);

        // Ждём именно событие выдачи: покупка тоже шлёт PurchaseUpdated,
        // и она может прийти уже после регистрации обработчика
        TaskCompletionSource<JsonElement> fulfilled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = connection.On<JsonElement>("PurchaseUpdated", payload =>
        {
            if (payload.GetProperty("isFulfilled").GetBoolean())
                fulfilled.TrySetResult(payload);
        });

        HttpResponseMessage buy = await _api.Client.PostAsync($"/api/shop/items/{itemId}/buy", null);
        Guid purchaseId = (await PartyAppApi.ReadJsonAsync(buy)).GetProperty("purchaseId").GetGuid();

        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/shop/purchases/{purchaseId}/fulfill", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        Task finished = await Task.WhenAny(fulfilled.Task, Task.Delay(Timeout));
        finished.Should().Be(fulfilled.Task, "админ выдал приз, игрок должен получить PurchaseUpdated");

        JsonElement payload = await fulfilled.Task;
        payload.GetProperty("purchaseId").GetGuid().Should().Be(purchaseId);
        payload.GetProperty("isFulfilled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task LotFinished_IsBroadcastWithWinner()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(admin);
        HttpResponseMessage created = await _api.Client.PostAsJsonAsync(
            "/api/shop/lots",
            new
            {
                name = "Торт",
                description = (string?)null,
                minBid = 10,
                durationMinutes = 30
            });
        Guid lotId = (await PartyAppApi.ReadJsonAsync(created)).GetProperty("id").GetGuid();
        (await _api.Client.PostAsync($"/api/shop/lots/{lotId}/start", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        _api.Authorize(player);
        await using HubConnection connection = await ConnectAsync(player);

        (await _api.Client.PostAsJsonAsync($"/api/shop/lots/{lotId}/bids", new { amount = 25 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Task<JsonElement> message = WaitForAsync(connection, "LotFinished");

        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/shop/lots/{lotId}/close", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("lotId").GetGuid().Should().Be(lotId);
        payload.GetProperty("winnerName").GetString().Should().Be(player.DisplayName);
        payload.GetProperty("winningBid").GetInt32().Should().Be(25);
    }

    [Fact]
    public async Task DareConfirmed_IsBroadcastWhenAdminConfirms()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        var definition = await _factory.DbAsync(db => db.EventDefinitions
            .AsNoTracking()
            .SingleAsync(d => d.Type == "dare"));

        _api.Authorize(admin);
        HttpResponseMessage start = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(start)).GetProperty("sessionId").GetGuid();

        _api.Authorize(player);
        await using HubConnection connection = await ConnectAsync(player);

        HttpResponseMessage draw = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = "{}" });
        draw.StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(admin);
        JsonElement pending = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/dare/pending"));
        Guid assignmentId = pending[0].GetProperty("id").GetGuid();

        Task<JsonElement> message = WaitForAsync(connection, "DareConfirmed");

        (await _api.Client.PostAsync($"/api/events/dare/{assignmentId}/confirm", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("playerId").GetGuid().Should().Be(player.Id);
        payload.GetProperty("points").GetInt32().Should().Be(5);
        payload.GetProperty("sessionId").GetGuid().Should().Be(sessionId);
    }

    [Fact]
    public async Task SessionRevoked_IsDeliveredToKickedPlayer()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        await using HubConnection connection = await ConnectAsync(player);
        Task<JsonElement> message = WaitForAsync(connection, "SessionRevoked");

        _api.Authorize(admin);
        HttpResponseMessage kick = await _api.Client.PostAsync($"/api/admin/players/{player.Id}/kick", null);
        kick.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("reason").GetString().Should().Be("Сессия завершена администратором");
    }

    [Fact]
    public async Task ProfileUpdated_IsBroadcastOnStatusChange()
    {
        TestUser player = await _api.RegisterAsync();
        await using HubConnection connection = await ConnectAsync(player);

        Task<JsonElement> message = WaitForAsync(connection, "ProfileUpdated");

        _api.Authorize(player);
        (await _api.Client.PatchAsJsonAsync("/api/profile", new { statusEmoji = "🎂" })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("userId").GetGuid().Should().Be(player.Id);
        payload.GetProperty("profileUpdatedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AchievementUnlocked_IsDeliveredToPlayer()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        EventDefinition definition = await _factory.SeedDefinitionAsync(
            "quiz",
            """{"timeLimitSec":300,"pointsPerCorrect":10,"questions":[{"text":"Q","options":["a","b"],"correctIndex":1}]}""",
            displayName: "Квиз достижения");

        _api.Authorize(admin);
        HttpResponseMessage started = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(started)).GetProperty("sessionId").GetGuid();

        _api.Authorize(player);
        await using HubConnection connection = await ConnectAsync(player);
        Task<JsonElement> message = WaitForAsync(connection, "AchievementUnlocked");

        (await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit",
            new { payloadJson = """{"questionIndex":0,"answerIndex":1}""" })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        JsonElement payload = await message;
        payload.GetProperty("code").GetString().Should().Be("first_answer");
        payload.GetProperty("points").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task PollUpdated_IsBroadcastOnCreateAndVote()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        EventDefinition first = await _factory.SeedDefinitionAsync("quiz", "{}", displayName: "Голосование 1");
        EventDefinition second = await _factory.SeedDefinitionAsync("raffle", "{}", displayName: "Голосование 2");

        await using HubConnection connection = await ConnectAsync(player);

        Task<JsonElement> createdMessage = WaitForAsync(connection, "PollUpdated");
        _api.Authorize(admin);
        (await _api.Client.PostAsJsonAsync(
            "/api/polls", new { question = "Что дальше?", definitionIds = new[] { first.Id, second.Id } })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        JsonElement poll = await createdMessage;
        Guid pollId = poll.GetProperty("id").GetGuid();
        Guid optionId = poll.GetProperty("options")[0].GetProperty("id").GetGuid();

        Task<JsonElement> votedMessage = WaitForAsync(connection, "PollUpdated");
        _api.Authorize(player);
        (await _api.Client.PostAsJsonAsync($"/api/polls/{pollId}/vote", new { optionId })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        JsonElement voted = await votedMessage;
        voted.GetProperty("totalVotes").GetInt32().Should().Be(1);
    }
}