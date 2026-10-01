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
    public async Task Confetti_AsAdmin_IncrementsVersion_AsPlayerForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(player);
        HttpResponseMessage forbidden = await _api.Client.PostAsync("/api/screen/confetti", null);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _api.Authorize(admin);
        HttpResponseMessage first = await _api.Client.PostAsync("/api/screen/confetti", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage second = await _api.Client.PostAsync("/api/screen/confetti", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        (await PartyAppApi.ReadJsonAsync(first)).GetProperty("version").GetInt32().Should().Be(1);
        (await PartyAppApi.ReadJsonAsync(second)).GetProperty("version").GetInt32().Should().Be(2);
    }
}