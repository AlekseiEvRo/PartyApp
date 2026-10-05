using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class AdminEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public AdminEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private Task<HttpResponseMessage> GrantAsync(Guid playerId, int amount, string? reason = null)
    {
        return _api.Client.PostAsJsonAsync("/api/admin/grant-points", new { playerId, amount, reason });
    }

    [Theory]
    [InlineData("/api/admin/players")]
    [InlineData("/api/admin/leaderboard")]
    [InlineData("/api/admin/sessions")]
    public async Task AdminEndpoints_AsPlayer_ReturnForbidden(string url)
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GrantPoints_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await GrantAsync(player.Id, 10);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GrantPoints_PositiveAmount_UpdatesBalanceAndWritesTransaction()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _factory.Push.Clear();
        _api.Authorize(admin);

        HttpResponseMessage response = await GrantAsync(player.Id, 50, "За лучший тост");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("newBalance").GetInt32().Should().Be(150);
        json.GetProperty("amount").GetInt32().Should().Be(50);
        json.GetProperty("reason").GetString().Should().Be("За лучший тост");

        (await _api.GetBalanceAsync(player)).Should().Be(150);

        await _factory.DbAsync(async db =>
        {
            WalletTransactionDto transaction = await db.WalletTransactions
                .Where(t => t.Wallet.UserId == player.Id && t.Type == WalletTransactionType.AdminGrant && t.Description == "За лучший тост")
                .Select(t => new WalletTransactionDto(t.Amount, t.Type, t.Description, t.RelatedSessionId))
                .SingleAsync();

            transaction.Amount.Should().Be(50);
            transaction.Type.Should().Be(WalletTransactionType.AdminGrant);
            transaction.Description.Should().Be("За лучший тост");
            transaction.RelatedSessionId.Should().BeNull();
        });

        PushCall push = _factory.Push.Calls.Should().ContainSingle().Subject;
        push.UserIds.Should().Equal(player.Id);
        push.Message.Title.Should().Be("⭐ +50 баллов");
        push.Message.Body.Should().Be("За лучший тост");
    }

    [Fact]
    public async Task GrantPoints_NegativeAmount_DeductsBalance()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await GrantAsync(player.Id, -30, "Штраф");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("newBalance").GetInt32().Should().Be(70);
        (await _factory.DbAsync(db => db.WalletTransactions
                .Where(t => t.Wallet.UserId == player.Id && t.Type == WalletTransactionType.AdminDeduct)
                .Select(t => t.Type)
                .SingleAsync()))
            .Should().Be(WalletTransactionType.AdminDeduct);
    }

    [Fact]
    public async Task GrantPoints_MoreThanBalance_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await GrantAsync(player.Id, -101);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Недостаточно баллов для списания у игрока");
        (await _api.GetBalanceAsync(player)).Should().Be(100);
    }

    [Fact]
    public async Task GrantPoints_ZeroAmount_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await GrantAsync(player.Id, 0);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Сумма не может быть 0");
    }

    [Fact]
    public async Task GrantPoints_UnknownPlayer_ReturnsNotFound()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await GrantAsync(Guid.NewGuid(), 10);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GrantPoints_WithoutReason_UsesDefaultDescription()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        await GrantAsync(player.Id, 5);

        (await _factory.DbAsync(db => db.WalletTransactions
                .Where(t => t.Wallet.UserId == player.Id && t.Type == WalletTransactionType.AdminGrant && t.Amount == 5)
                .Select(t => t.Description)
                .SingleAsync()))
            .Should().Be("Ручное начисление");
    }

    [Fact]
    public async Task Players_AsAdmin_ReturnsAllUsersWithBalancesSortedDescending()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser low = await _api.RegisterAsync();
        TestUser high = await _api.RegisterAsync();
        _api.Authorize(admin);
        await GrantAsync(high.Id, 50);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/players");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        List<JsonElement> players = json.EnumerateArray().ToList();

        int balanceOf(TestUser user) => players.Single(p => p.GetProperty("id").GetGuid() == user.Id)
            .GetProperty("balance").GetInt32();
        int positionOf(TestUser user) => players.FindIndex(p => p.GetProperty("id").GetGuid() == user.Id);

        balanceOf(low).Should().Be(100);
        balanceOf(high).Should().Be(150);
        positionOf(high).Should().BeLessThan(positionOf(low));
        players.Select(p => p.GetProperty("balance").GetInt32())
            .Should().BeInDescendingOrder();

        JsonElement adminRow = players.Single(p => p.GetProperty("id").GetGuid() == admin.Id);
        adminRow.GetProperty("role").GetString().Should().Be("Admin");
        adminRow.GetProperty("username").GetString().Should().Be(admin.Username);
        adminRow.GetProperty("displayName").GetString().Should().Be(admin.DisplayName);
    }

    [Fact]
    public async Task Leaderboard_AsAdmin_ReturnsTopPlayers()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);
        await GrantAsync(player.Id, 25);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/leaderboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<JsonElement> rows = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray().ToList();
        rows.Count.Should().BeLessThanOrEqualTo(20);

        JsonElement row = rows.Single(r => r.GetProperty("id").GetGuid() == player.Id);
        row.GetProperty("displayName").GetString().Should().Be(player.DisplayName);
        row.GetProperty("balance").GetInt32().Should().Be(125);

        List<int> balances = rows.Select(r => r.GetProperty("balance").GetInt32()).ToList();
        balances.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Leaderboard_DoesNotIncludeAdmins()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);
        await GrantAsync(admin.Id, 500);
        await GrantAsync(player.Id, 25);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/leaderboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<JsonElement> rows = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray().ToList();

        rows.Should().NotContain(r => r.GetProperty("id").GetGuid() == admin.Id);
        rows.Should().Contain(r => r.GetProperty("id").GetGuid() == player.Id);
    }

    [Fact]
    public async Task Sessions_AsAdmin_ReturnsStartedSessionsWithDefinitionData()
    {
        TestUser admin = await _api.CreateAdminAsync();
        EventDefinition definition = await _factory.SeedDefinitionAsync("quiz", displayName: "Квиз админа");
        _api.Authorize(admin);

        HttpResponseMessage start = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(start)).GetProperty("sessionId").GetGuid();

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<JsonElement> sessions = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray().ToList();
        JsonElement session = sessions.Single(s => s.GetProperty("id").GetGuid() == sessionId);
        session.GetProperty("definitionId").GetGuid().Should().Be(definition.Id);
        session.GetProperty("definitionName").GetString().Should().Be("Квиз админа");
        session.GetProperty("type").GetString().Should().Be("quiz");
        session.GetProperty("state").GetString().Should().Be("Active");
        session.GetProperty("startedBy").GetString().Should().Be(admin.DisplayName);
        session.GetProperty("submissionCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task PlayerTransactions_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync($"/api/admin/players/{player.Id}/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlayerTransactions_AsAdmin_ReturnsHistoryNewestFirst()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);
        await GrantAsync(player.Id, 50, "За тост");

        HttpResponseMessage response = await _api.Client.GetAsync($"/api/admin/players/{player.Id}/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("total").GetInt32().Should().Be(2);
        json.GetProperty("player").GetProperty("displayName").GetString().Should().Be(player.DisplayName);

        JsonElement newest = json.GetProperty("items")[0];
        newest.GetProperty("amount").GetInt32().Should().Be(50);
        newest.GetProperty("type").GetString().Should().Be("AdminGrant");
        newest.GetProperty("description").GetString().Should().Be("За тост");

        JsonElement oldest = json.GetProperty("items")[1];
        oldest.GetProperty("description").GetString().Should().Be("Приветственный бонус");
    }

    [Fact]
    public async Task PlayerTransactions_UnknownPlayer_ReturnsNotFound()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.GetAsync($"/api/admin/players/{Guid.NewGuid()}/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PlayerTransactions_WithoutWallet_ReturnsEmpty()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        await _factory.DbAsync(async db =>
        {
            await db.Wallets.Where(w => w.UserId == player.Id).ExecuteDeleteAsync();
        });
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.GetAsync($"/api/admin/players/{player.Id}/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("total").GetInt32().Should().Be(0);
        json.GetProperty("items").GetArrayLength().Should().Be(0);

        // Список игроков тоже не должен падать без кошелька
        HttpResponseMessage playersResponse = await _api.Client.GetAsync("/api/admin/players");
        playersResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement players = await PartyAppApi.ReadJsonAsync(playersResponse);
        players.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == player.Id)
            .GetProperty("balance").GetInt32().Should().Be(0);
    }

    private sealed record WalletTransactionDto(int Amount, WalletTransactionType Type, string Description, Guid? RelatedSessionId);
}