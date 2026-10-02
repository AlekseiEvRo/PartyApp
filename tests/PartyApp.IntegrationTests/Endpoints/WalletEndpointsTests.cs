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

    [Fact]
    public async Task Players_ReturnsEveryoneExceptCurrentUser()
    {
        TestUser me = await _api.RegisterAsync();
        TestUser other = await _api.RegisterAsync();

        _api.Authorize(me);
        HttpResponseMessage response = await _api.Client.GetAsync("/api/wallet/players");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<JsonElement> players = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray().ToList();
        players.Select(p => p.GetProperty("id").GetGuid()).Should().Contain(other.Id).And.NotContain(me.Id);
        players.Select(p => p.GetProperty("displayName").GetString()).Should().Contain(other.DisplayName);
    }

    [Fact]
    public async Task Transfer_MovesPointsAndWritesBothHistories()
    {
        TestUser sender = await _api.RegisterAsync();
        TestUser recipient = await _api.RegisterAsync();
        _factory.Push.Clear();

        _api.Authorize(sender);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/wallet/transfer", new { recipientId = recipient.Id, amount = 30, comment = "На такси" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("newBalance").GetInt32().Should().Be(70);
        json.GetProperty("recipientBalance").GetInt32().Should().Be(130);

        (await _api.GetBalanceAsync(sender)).Should().Be(70);
        (await _api.GetBalanceAsync(recipient)).Should().Be(130);

        _api.Authorize(sender);
        JsonElement senderHistory = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/wallet/transactions"));
        JsonElement outgoing = senderHistory.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("type").GetString() == "TransferOut");
        outgoing.GetProperty("amount").GetInt32().Should().Be(-30);
        outgoing.GetProperty("description").GetString()
            .Should().Be($"Перевод игроку {recipient.DisplayName}: На такси");

        _api.Authorize(recipient);
        JsonElement recipientHistory = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/wallet/transactions"));
        JsonElement incoming = recipientHistory.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("type").GetString() == "TransferIn");
        incoming.GetProperty("amount").GetInt32().Should().Be(30);
        incoming.GetProperty("description").GetString()
            .Should().Be($"Перевод от {sender.DisplayName}: На такси");

        PushCall senderPush = _factory.Push.Calls.Single(c => c.UserIds != null && c.UserIds.Contains(sender.Id));
        senderPush.Message.Title.Should().Be("−30 баллов");
        PushCall recipientPush = _factory.Push.Calls.Single(c => c.UserIds != null && c.UserIds.Contains(recipient.Id));
        recipientPush.Message.Title.Should().Be("⭐ +30 баллов");
    }

    [Fact]
    public async Task Transfer_MoreThanBalance_ReturnsBadRequest()
    {
        TestUser sender = await _api.RegisterAsync();
        TestUser recipient = await _api.RegisterAsync();

        _api.Authorize(sender);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/wallet/transfer", new { recipientId = recipient.Id, amount = 101 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Недостаточно баллов для перевода");
    }

    [Fact]
    public async Task Transfer_ToSelf_ReturnsBadRequest()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/wallet/transfer", new { recipientId = user.Id, amount = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Transfer_UnknownRecipient_ReturnsNotFound()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/wallet/transfer", new { recipientId = Guid.NewGuid(), amount = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfer_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/wallet/transfer", new { recipientId = Guid.NewGuid(), amount = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}