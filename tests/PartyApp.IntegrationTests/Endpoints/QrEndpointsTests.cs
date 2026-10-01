using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class QrEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public QrEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private Task<HttpResponseMessage> GenerateAsync(int count, int points)
    {
        return _api.Client.PostAsJsonAsync("/api/qr/tokens/generate", new { count, points });
    }

    private async Task<JsonElement> GenerateSingleTokenAsync(TestUser admin, int points = 25)
    {
        _api.Authorize(admin);
        HttpResponseMessage response = await GenerateAsync(1, points);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await PartyAppApi.ReadJsonAsync(response)).GetProperty("tokens")[0];
    }

    private async Task<Guid> StartQrScanEventAsync(TestUser admin)
    {
        EventDefinition definition = await _factory.DbAsync(db => db.EventDefinitions
            .AsNoTracking()
            .SingleAsync(d => d.Type == "qr_scan" && d.IsActive));

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await PartyAppApi.ReadJsonAsync(response)).GetProperty("sessionId").GetGuid();
    }

    [Fact]
    public async Task Tokens_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/qr/tokens");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tokens_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/qr/tokens");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Generate_AsAdmin_CreatesUniqueSafeCodes()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await GenerateAsync(3, 25);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("generated").GetInt32().Should().Be(3);

        List<string> codes = json.GetProperty("tokens").EnumerateArray()
            .Select(t => t.GetProperty("code").GetString()!)
            .ToList();
        codes.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        codes.Should().OnlyContain(code => code.Length == 6);
        codes.Should().OnlyContain(code => code.All(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c)));
        json.GetProperty("tokens").EnumerateArray().Should().OnlyContain(t => t.GetProperty("points").GetInt32() == 25);
    }

    [Fact]
    public async Task GetTokens_AsAdmin_ReturnsGeneratedTokens()
    {
        TestUser admin = await _api.CreateAdminAsync();
        JsonElement token = await GenerateSingleTokenAsync(admin, points: 40);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/qr/tokens");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement listed = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == token.GetProperty("id").GetGuid());
        listed.GetProperty("code").GetString().Should().Be(token.GetProperty("code").GetString());
        listed.GetProperty("points").GetInt32().Should().Be(40);
        listed.GetProperty("isRedeemed").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Generate_WithOutOfRangeCount_ReturnsServerError(int count)
    {
        // Известное ограничение: QrTokenService бросает InvalidOperationException,
        // а middleware маппит в 500. Тест фиксирует текущее поведение.
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await GenerateAsync(count, 10);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task FullRedeemFlow_TokenCanBeUsedOnlyOnce()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        TestUser otherPlayer = await _api.RegisterAsync();
        JsonElement token = await GenerateSingleTokenAsync(admin, points: 25);
        string code = token.GetProperty("code").GetString()!;
        Guid sessionId = await StartQrScanEventAsync(admin);

        _api.Authorize(player);
        HttpResponseMessage redeem = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = $$"""{"code":"{{code}}"}""" });

        redeem.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement redeemJson = await PartyAppApi.ReadJsonAsync(redeem);
        redeemJson.GetProperty("pointsAwarded").GetInt32().Should().Be(25);
        redeemJson.GetProperty("message").GetString().Should().Be("QR-код активирован! +25 баллов");
        (await _api.GetBalanceAsync(player)).Should().Be(125);

        QrToken stored = await _factory.DbAsync(db => db.QrTokens
            .AsNoTracking()
            .SingleAsync(t => t.Code == code));
        stored.RedeemedById.Should().Be(player.Id);
        stored.RedeemedAt.Should().NotBeNull();

        // Второй игрок тем же кодом
        _api.Authorize(otherPlayer);
        HttpResponseMessage second = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = $$"""{"code":"{{code}}"}""" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(second)).GetProperty("error").GetString()
            .Should().Be("Этот код уже был использован");
        (await _api.GetBalanceAsync(otherPlayer)).Should().Be(100);
    }

    [Fact]
    public async Task Redeem_LowercaseCode_IsAccepted()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        JsonElement token = await GenerateSingleTokenAsync(admin, points: 15);
        string code = token.GetProperty("code").GetString()!;
        Guid sessionId = await StartQrScanEventAsync(admin);

        _api.Authorize(player);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit",
            new { payloadJson = $$"""{"code":"{{code.ToLowerInvariant()}}"}""" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.GetBalanceAsync(player)).Should().Be(115);
    }

    [Fact]
    public async Task Redeem_UnknownCode_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid sessionId = await StartQrScanEventAsync(admin);

        _api.Authorize(player);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = """{"code":"NOPE42"}""" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString().Should().Be("Код не найден");
    }

    [Fact]
    public async Task Redeem_TokenBecomesRedeemedInAdminList()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        JsonElement token = await GenerateSingleTokenAsync(admin, points: 10);
        string code = token.GetProperty("code").GetString()!;
        Guid sessionId = await StartQrScanEventAsync(admin);

        _api.Authorize(player);
        await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = $$"""{"code":"{{code}}"}""" });

        _api.Authorize(admin);
        HttpResponseMessage list = await _api.Client.GetAsync("/api/qr/tokens");
        JsonElement redeemed = (await PartyAppApi.ReadJsonAsync(list)).EnumerateArray()
            .Single(t => t.GetProperty("code").GetString() == code);
        redeemed.GetProperty("isRedeemed").GetBoolean().Should().BeTrue();
        redeemed.GetProperty("redeemedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }
}