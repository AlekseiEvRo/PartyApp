using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class PushEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public PushEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private static string FcmEndpoint(string suffix = "abc") => $"https://fcm.googleapis.com/fcm/send/{suffix}";

    private Task<HttpResponseMessage> SubscribeAsync(string endpoint, string p256dh = "key", string auth = "auth", string? userAgent = null)
    {
        return _api.Client.PostAsJsonAsync(
            "/api/push/subscribe",
            new { endpoint, p256dh, auth, userAgent });
    }

    [Fact]
    public async Task VapidPublicKey_IsPublic()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/push/vapid-public-key");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("publicKey").GetString()
            .Should().Be("test-vapid-public-key");
    }

    [Fact]
    public async Task Subscribe_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/push/subscribe",
            new { endpoint = FcmEndpoint(), p256dh = "key", auth = "auth" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("https://evil.example.com/push")]
    [InlineData("http://fcm.googleapis.com.evil.com/push")]
    [InlineData("ftp://fcm.googleapis.com/push")]
    [InlineData("not-a-url")]
    public async Task Subscribe_WithForeignEndpoint_ReturnsBadRequest(string endpoint)
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await SubscribeAsync(endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("", "auth")]
    [InlineData("key", "")]
    public async Task Subscribe_WithoutKeys_ReturnsBadRequest(string p256dh, string auth)
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await SubscribeAsync(FcmEndpoint(), p256dh, auth);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Subscribe_WithAllowedEndpoint_SavesSubscription()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await SubscribeAsync(FcmEndpoint("save"), userAgent: "Test Phone");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("success").GetBoolean().Should().BeTrue();

        PushSubscription subscription = await _factory.DbAsync(db => db.PushSubscriptions
            .AsNoTracking()
            .SingleAsync(s => s.Endpoint == FcmEndpoint("save")));
        subscription.UserId.Should().Be(user.Id);
        subscription.P256dh.Should().Be("key");
        subscription.Auth.Should().Be("auth");
        subscription.UserAgent.Should().Be("Test Phone");
    }

    [Fact]
    public async Task Subscribe_WithTooLongUserAgent_TruncatesIt()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);
        string longUserAgent = new string('u', 500);

        HttpResponseMessage response = await SubscribeAsync(FcmEndpoint("long-agent"), userAgent: longUserAgent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string? stored = await _factory.DbAsync(db => db.PushSubscriptions
            .AsNoTracking()
            .Where(s => s.Endpoint == FcmEndpoint("long-agent"))
            .Select(s => s.UserAgent)
            .SingleAsync());
        stored!.Length.Should().Be(300);
    }

    [Fact]
    public async Task Subscribe_SameEndpointForAnotherUser_RebindsSubscription()
    {
        TestUser first = await _api.RegisterAsync();
        TestUser second = await _api.RegisterAsync();
        _api.Authorize(first);
        await SubscribeAsync(FcmEndpoint("rebind"));

        _api.Authorize(second);
        HttpResponseMessage response = await SubscribeAsync(FcmEndpoint("rebind"), p256dh: "new-key", auth: "new-auth");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PushSubscription[] subscriptions = await _factory.DbAsync(db => db.PushSubscriptions
            .AsNoTracking()
            .Where(s => s.Endpoint == FcmEndpoint("rebind"))
            .ToArrayAsync());
        subscriptions.Should().ContainSingle();
        subscriptions[0].UserId.Should().Be(second.Id);
        subscriptions[0].P256dh.Should().Be("new-key");
        subscriptions[0].Auth.Should().Be("new-auth");
    }

    [Fact]
    public async Task Subscribe_UsesUserAgentHeader_WhenBodyIsEmpty()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);
        _api.Client.DefaultRequestHeaders.UserAgent.ParseAdd("PartyApp/1.0 (test)");

        HttpResponseMessage response = await SubscribeAsync(FcmEndpoint("header-agent"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string? stored = await _factory.DbAsync(db => db.PushSubscriptions
            .AsNoTracking()
            .Where(s => s.Endpoint == FcmEndpoint("header-agent"))
            .Select(s => s.UserAgent)
            .SingleAsync());
        stored.Should().Contain("PartyApp/1.0");
    }

    [Fact]
    public async Task Unsubscribe_RemovesSubscription()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);
        await SubscribeAsync(FcmEndpoint("remove"));

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/push/unsubscribe", new { endpoint = FcmEndpoint("remove") });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.DbAsync(db => db.PushSubscriptions
            .AsNoTracking()
            .AnyAsync(s => s.Endpoint == FcmEndpoint("remove"))))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Unsubscribe_UnknownEndpoint_IsIdempotent()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/push/unsubscribe", new { endpoint = FcmEndpoint("missing") });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unsubscribe_WithoutEndpoint_ReturnsBadRequest()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/push/unsubscribe", new { endpoint = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Test_ReturnsDeliveryReportFromPushService()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsync("/api/push/test", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("sent").GetInt32().Should().Be(2);
        json.GetProperty("failed").GetInt32().Should().Be(0);

        PushCall push = _factory.Push.Calls.Last();
        push.UserIds.Should().Equal(user.Id);
        push.Message.Tag.Should().Be("push-test");
    }

    [Fact]
    public async Task Test_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/push/test", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}