using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class NotificationsEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public NotificationsEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    [Fact]
    public async Task Broadcast_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/notifications/broadcast", new { message = "Всем привет" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Broadcast_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/notifications/broadcast", new { message = "Всем привет" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Broadcast_AsAdmin_SendsPushToEveryone()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);
        _factory.Push.Clear();

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/notifications/broadcast", new { message = "Скоро торт!" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("success").GetBoolean().Should().BeTrue();

        PushCall push = _factory.Push.Calls.Should().ContainSingle().Subject;
        push.UserIds.Should().BeNull();
        push.ExcludedUserId.Should().BeNull();
        push.Message.Title.Should().Be("📢 Сообщение от ведущего");
        push.Message.Body.Should().Be("Скоро торт!");
        push.Message.Tag.Should().Be("broadcast");
    }
}