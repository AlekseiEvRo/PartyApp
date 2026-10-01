using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class WalletEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public WalletEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    [Fact]
    public async Task Balance_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/wallet/balance");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Balance_AfterRegistration_ReturnsWelcomeBonus()
    {
        TestUser user = await _api.RegisterAsync();

        int balance = await _api.GetBalanceAsync(user);

        balance.Should().Be(100);
    }

    [Fact]
    public async Task Balance_WithoutWallet_ReturnsZero()
    {
        TestUser user = await _api.RegisterAsync();
        await _factory.DbAsync(async db =>
        {
            await db.Wallets.Where(w => w.UserId == user.Id).ExecuteDeleteAsync();
        });

        int balance = await _api.GetBalanceAsync(user);

        balance.Should().Be(0);
    }

    [Fact]
    public async Task Transactions_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/wallet/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Transactions_AfterRegistration_ContainsWelcomeBonus()
    {
        TestUser user = await _api.RegisterAsync();

        _api.Authorize(user);
        HttpResponseMessage response = await _api.Client.GetAsync("/api/wallet/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("total").GetInt32().Should().Be(1);

        JsonElement item = json.GetProperty("items")[0];
        item.GetProperty("amount").GetInt32().Should().Be(100);
        item.GetProperty("type").GetString().Should().Be("AdminGrant");
        item.GetProperty("description").GetString().Should().Be("Приветственный бонус");
    }

    [Fact]
    public async Task Transactions_WithoutWallet_ReturnsEmptyList()
    {
        TestUser user = await _api.RegisterAsync();
        await _factory.DbAsync(async db =>
        {
            await db.Wallets.Where(w => w.UserId == user.Id).ExecuteDeleteAsync();
        });

        _api.Authorize(user);
        HttpResponseMessage response = await _api.Client.GetAsync("/api/wallet/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("total").GetInt32().Should().Be(0);
        json.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Transactions_SupportsPagingAndKeepsPagesDisjoint()
    {
        TestUser user = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(admin);
        (await _api.Client.PostAsJsonAsync("/api/admin/grant-points",
                new { playerId = user.Id, amount = 5, reason = "Тест 1" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.Client.PostAsJsonAsync("/api/admin/grant-points",
                new { playerId = user.Id, amount = -3, reason = "Тест 2" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(user);
        JsonElement page1 = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/wallet/transactions?limit=2&offset=0"));
        JsonElement page2 = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/wallet/transactions?limit=2&offset=2"));

        page1.GetProperty("total").GetInt32().Should().Be(3);
        page1.GetProperty("items").GetArrayLength().Should().Be(2);
        page2.GetProperty("total").GetInt32().Should().Be(3);
        page2.GetProperty("items").GetArrayLength().Should().Be(1);

        Guid[] page1Ids = page1.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToArray();
        Guid[] page2Ids = page2.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToArray();
        page1Ids.Should().NotIntersectWith(page2Ids);
    }
}