using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

/// <summary>
/// ScreenService — singleton с состоянием в памяти, поэтому каждый тест
/// работает со свежим приложением.
/// </summary>
public class ScreenEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public ScreenEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task GetState_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/screen/state");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetState_AsPlayer_ReturnsIdleState()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/screen/state");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement state = await PartyAppApi.ReadJsonAsync(response);
        state.GetProperty("mode").GetString().Should().Be("idle");
        state.GetProperty("version").GetInt32().Should().Be(0);
        state.GetProperty("serverTimeUtc").GetDateTime().Should()
            .BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SetState_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "leaderboard" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SetState_AsAdmin_UpdatesStateAndValidatesInput()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        // Лидерборд
        HttpResponseMessage leaderboard = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "leaderboard" });
        leaderboard.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement state = await PartyAppApi.ReadJsonAsync(leaderboard);
        state.GetProperty("mode").GetString().Should().Be("leaderboard");
        state.GetProperty("version").GetInt32().Should().Be(1);

        // Сообщение
        HttpResponseMessage message = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "message", message = "  Все танцуем!  " });
        message.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement messageState = await PartyAppApi.ReadJsonAsync(message);
        messageState.GetProperty("message").GetString().Should().Be("Все танцуем!");
        messageState.GetProperty("version").GetInt32().Should().Be(2);

        // GET отдаёт последнее состояние
        HttpResponseMessage current = await _api.Client.GetAsync("/api/screen/state");
        JsonElement currentState = await PartyAppApi.ReadJsonAsync(current);
        currentState.GetProperty("mode").GetString().Should().Be("message");
        currentState.GetProperty("version").GetInt32().Should().Be(2);

        // Неизвестный режим
        HttpResponseMessage unknownMode = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "disco" });
        unknownMode.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Режим ивента требует sessionId
        HttpResponseMessage missingSession = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "event" });
        missingSession.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Несуществующая сессия
        HttpResponseMessage unknownSession = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "event", sessionId = Guid.NewGuid() });
        unknownSession.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Пустое сообщение
        HttpResponseMessage emptyMessage = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "message", message = "   " });
        emptyMessage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetState_EventMode_WithExistingSession_Works()
    {
        TestUser admin = await _api.CreateAdminAsync();
        var definition = await _factory.DbAsync(db => db.EventDefinitions
            .FirstAsync(d => d.Type == "quiz"));

        _api.Authorize(admin);
        HttpResponseMessage start = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(start)).GetProperty("sessionId").GetGuid();

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "event", sessionId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement state = await PartyAppApi.ReadJsonAsync(response);
        state.GetProperty("mode").GetString().Should().Be("event");
        state.GetProperty("sessionId").GetGuid().Should().Be(sessionId);
    }

    [Fact]
    public async Task Confetti_IsAllowedForPlayersAndIncrementsVersion()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        // Теперь конфетти может запустить любой игрок
        _api.Authorize(player);
        HttpResponseMessage playerResponse = await _api.Client.PostAsync("/api/screen/confetti", null);
        playerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(admin);
        HttpResponseMessage adminResponse = await _api.Client.PostAsync("/api/screen/confetti", null);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        (await PartyAppApi.ReadJsonAsync(playerResponse)).GetProperty("version").GetInt32().Should().Be(1);
        (await PartyAppApi.ReadJsonAsync(adminResponse)).GetProperty("version").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Reactions_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/screen/reactions", new { emoji = "🎉" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reactions_AsPlayer_AreValidatedAndAccepted()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        // Пустая реакция не принимается
        HttpResponseMessage empty = await _api.Client.PostAsJsonAsync("/api/screen/reactions", new { });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Стикер
        HttpResponseMessage sticker = await _api.Client.PostAsJsonAsync(
            "/api/screen/reactions", new { emoji = "🎉" });
        sticker.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement stickerJson = await PartyAppApi.ReadJsonAsync(sticker);
        stickerJson.GetProperty("emoji").GetString().Should().Be("🎉");
        stickerJson.GetProperty("authorName").GetString().Should().Be(player.DisplayName);

        // Длинный текст обрезается
        HttpResponseMessage text = await _api.Client.PostAsJsonAsync(
            "/api/screen/reactions", new { emoji = "❤️", text = new string('а', 200) });
        text.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(text)).GetProperty("text").GetString()!.Length.Should().Be(120);

        // Слишком длинный стикер — ошибка
        HttpResponseMessage longEmoji = await _api.Client.PostAsJsonAsync(
            "/api/screen/reactions", new { emoji = new string('x', 17) });
        longEmoji.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Settings_DefaultValuesThenAdminCanUpdate()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(player);
        JsonElement defaults = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/screen/settings"));
        defaults.GetProperty("photoSeconds").GetInt32().Should().Be(8);
        defaults.GetProperty("leaderboardSeconds").GetInt32().Should().Be(60);
        defaults.GetProperty("shopSeconds").GetInt32().Should().Be(60);

        // Игрок менять не может
        HttpResponseMessage forbidden = await _api.Client.PutAsJsonAsync(
            "/api/screen/settings",
            new { photoSeconds = 10, leaderboardSeconds = 30, shopSeconds = 30 });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _api.Authorize(admin);
        HttpResponseMessage update = await _api.Client.PutAsJsonAsync(
            "/api/screen/settings",
            new { photoSeconds = 10, leaderboardSeconds = 30, shopSeconds = 45 });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement updated = await PartyAppApi.ReadJsonAsync(update);
        updated.GetProperty("leaderboardSeconds").GetInt32().Should().Be(30);

        JsonElement stored = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/screen/settings"));
        stored.GetProperty("shopSeconds").GetInt32().Should().Be(45);

        // Некорректные значения
        HttpResponseMessage invalid = await _api.Client.PutAsJsonAsync(
            "/api/screen/settings",
            new { photoSeconds = 1, leaderboardSeconds = 30, shopSeconds = 30 });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetState_AcceptsShopAndRotationModes()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage shop = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "shop" });
        shop.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(shop)).GetProperty("mode").GetString().Should().Be("shop");

        HttpResponseMessage rotation = await _api.Client.PostAsJsonAsync(
            "/api/screen/state", new { mode = "rotation" });
        rotation.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(rotation)).GetProperty("mode").GetString().Should().Be("rotation");
    }
}