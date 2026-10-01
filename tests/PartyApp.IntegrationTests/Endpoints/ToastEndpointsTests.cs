using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

/// <summary>
/// ToastService — singleton с общим кулдауном, поэтому каждый тест
/// поднимает отдельное приложение и не зависит от соседних тестов.
/// </summary>
public class ToastEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public ToastEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private Task<HttpResponseMessage> SayToastAsync(TestUser user)
    {
        _api.Authorize(user);
        return _api.Client.PostAsync("/api/toast/say", null);
    }

    [Fact]
    public async Task Say_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/toast/say", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Say_FirstToast_AwardsPointAndReturnsBusyUntil()
    {
        TestUser speaker = await _api.RegisterAsync();

        HttpResponseMessage response = await SayToastAsync(speaker);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("message").GetString().Should().Be("Тост засчитан!");
        json.GetProperty("pointsAwarded").GetInt32().Should().Be(1);
        json.GetProperty("busyUntilUtc").GetDateTime().Should().BeAfter(DateTime.UtcNow);

        (await _api.GetBalanceAsync(speaker)).Should().Be(101);
    }

    [Fact]
    public async Task Say_SecondToastDuringCooldown_ReturnsConflict()
    {
        TestUser first = await _api.RegisterAsync();
        TestUser second = await _api.RegisterAsync();

        await SayToastAsync(first);
        HttpResponseMessage response = await SayToastAsync(second);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("error").GetString().Should().Be($"Тост уже говорит {first.Username}");
        json.GetProperty("busyByName").GetString().Should().Be(first.Username);
        json.GetProperty("busyUntilUtc").GetDateTime().Should().BeAfter(DateTime.UtcNow);

        (await _api.GetBalanceAsync(second)).Should().Be(100);
    }

    [Fact]
    public async Task Say_PushesToEveryoneExceptSpeaker()
    {
        TestUser speaker = await _api.RegisterAsync();
        _factory.Push.Clear();

        await SayToastAsync(speaker);

        PushCall push = _factory.Push.Calls.Single(c => c.Message.Tag == "toast");
        push.ExcludedUserId.Should().Be(speaker.Id);
        push.UserIds.Should().BeNull();
        push.Message.Title.Should().Be("🍾 Тосты!");
        push.Message.Body.Should().Be($"{speaker.Username} говорит тост — скорее слушай!");
    }

    [Fact]
    public async Task Status_BeforeAnyToast_ReturnsFree()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/toast/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("isBusy").GetBoolean().Should().BeFalse();
        json.GetProperty("busyUntilUtc").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("currentSpeakerName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Status_DuringToast_ReturnsSpeaker()
    {
        TestUser speaker = await _api.RegisterAsync();
        await SayToastAsync(speaker);
        _api.Authorize(speaker);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/toast/status");

        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("isBusy").GetBoolean().Should().BeTrue();
        json.GetProperty("currentSpeakerName").GetString().Should().Be(speaker.Username);
    }

    [Fact]
    public async Task Say_WithoutToken_DoesNotConsumeCooldown()
    {
        TestUser speaker = await _api.RegisterAsync();
        using HttpClient anonymous = _factory.CreateClient();
        await anonymous.PostAsync("/api/toast/say", null);

        HttpResponseMessage response = await SayToastAsync(speaker);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}