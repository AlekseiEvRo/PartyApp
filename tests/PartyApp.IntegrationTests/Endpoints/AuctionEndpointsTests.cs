using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class AuctionEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public AuctionEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<Guid> CreateLotAsync(
        TestUser admin,
        int minBid = 10,
        int durationMinutes = 30,
        string name = "Торт",
        bool start = true)
    {
        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/shop/lots",
            new
            {
                name,
                description = "Спор за лучший кусок",
                minBid,
                durationMinutes
            });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        Guid lotId = (await PartyAppApi.ReadJsonAsync(response)).GetProperty("id").GetGuid();

        if (start)
        {
            _api.Authorize(admin);
            (await _api.Client.PostAsync($"/api/shop/lots/{lotId}/start", null)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        return lotId;
    }

    private async Task<HttpResponseMessage> BidAsync(TestUser player, Guid lotId, int amount)
    {
        _api.Authorize(player);
        return await _api.Client.PostAsJsonAsync($"/api/shop/lots/{lotId}/bids", new { amount });
    }

    [Fact]
    public async Task Lots_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/shop/lots");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateLot_WithInvalidData_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage zeroBid = await _api.Client.PostAsJsonAsync(
            "/api/shop/lots", new { name = "Лот", minBid = 0, durationMinutes = 30 });

        HttpResponseMessage zeroDuration = await _api.Client.PostAsJsonAsync(
            "/api/shop/lots", new { name = "Лот", minBid = 10, durationMinutes = 0 });

        zeroBid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        zeroDuration.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DraftLot_IsHiddenAndAcceptsNoBidsUntilStarted()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid lotId = await CreateLotAsync(admin, start: false);

        // В список открытых лотов черновик не попадает
        _api.Authorize(player);
        JsonElement open = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync("/api/shop/lots"));
        open.GetArrayLength().Should().Be(0);

        (await BidAsync(player, lotId, 20)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // После «Начать» лот появляется и принимает ставки
        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/shop/lots/{lotId}/start", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        _api.Authorize(player);
        (await BidAsync(player, lotId, 20)).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement after = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync("/api/shop/lots"));
        after.GetArrayLength().Should().Be(1);
        after[0].GetProperty("topBid").GetInt32().Should().Be(20);
        after[0].GetProperty("leaderName").GetString().Should().Be(player.DisplayName);
        after[0].GetProperty("endsAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task BidFlow_SealedBids_WinnerTakesLot_LoserRefunded()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser alice = await _api.RegisterAsync();
        TestUser boris = await _api.RegisterAsync();
        Guid lotId = await CreateLotAsync(admin);

        // Алиса ставит 40, Борис перебивает 70
        (await BidAsync(alice, lotId, 40)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await BidAsync(boris, lotId, 70)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.GetBalanceAsync(alice)).Should().Be(60);
        (await _api.GetBalanceAsync(boris)).Should().Be(30);

        // Открытый аукцион: игроки видят лидера и его ставку
        _api.Authorize(alice);
        JsonElement lots = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/lots"));
        lots.GetArrayLength().Should().Be(1);
        lots[0].GetProperty("myBid").GetInt32().Should().Be(40);
        lots[0].GetProperty("bidsCount").GetInt32().Should().Be(2);
        lots[0].GetProperty("topBid").GetInt32().Should().Be(70);
        lots[0].GetProperty("leaderName").GetString().Should().Be(boris.DisplayName);
        lots[0].TryGetProperty("winningBid", out _).Should().BeFalse();

        _api.Authorize(boris);
        JsonElement borisView = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/lots"));
        borisView[0].GetProperty("myBid").GetInt32().Should().Be(70);

        // Админ закрывает лот: побеждает Борис, Алиса получает возврат
        _api.Authorize(admin);
        HttpResponseMessage close = await _api.Client.PostAsync($"/api/shop/lots/{lotId}/close", null);
        close.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement summary = await PartyAppApi.ReadJsonAsync(close);
        summary.GetProperty("winnerName").GetString().Should().Be(boris.DisplayName);
        summary.GetProperty("winningBid").GetInt32().Should().Be(70);

        (await _api.GetBalanceAsync(alice)).Should().Be(100);
        (await _api.GetBalanceAsync(boris)).Should().Be(30);

        // В списке открытых лотов больше нет
        JsonElement openLots = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/lots"));
        openLots.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Bid_RaisingOwnBid_ChargesOnlyDifference()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid lotId = await CreateLotAsync(admin);

        (await BidAsync(player, lotId, 30)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await BidAsync(player, lotId, 55)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.GetBalanceAsync(player)).Should().Be(45);

        HttpResponseMessage same = await BidAsync(player, lotId, 55);
        same.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Bid_AfterClose_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid lotId = await CreateLotAsync(admin);

        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/shop/lots/{lotId}/close", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        HttpResponseMessage response = await BidAsync(player, lotId, 20);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancel_RefundsEveryBidder()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser alice = await _api.RegisterAsync();
        TestUser boris = await _api.RegisterAsync();
        Guid lotId = await CreateLotAsync(admin);

        await BidAsync(alice, lotId, 25);
        await BidAsync(boris, lotId, 35);

        _api.Authorize(admin);
        HttpResponseMessage cancel = await _api.Client.PostAsync($"/api/shop/lots/{lotId}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.GetBalanceAsync(alice)).Should().Be(100);
        (await _api.GetBalanceAsync(boris)).Should().Be(100);
    }

    [Fact]
    public async Task AdminList_ShowsBidsAndPlayers()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid lotId = await CreateLotAsync(admin);

        await BidAsync(player, lotId, 45);

        _api.Authorize(admin);
        JsonElement lots = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/lots/all"));

        lots.GetArrayLength().Should().Be(1);
        JsonElement bid = lots[0].GetProperty("bids")[0];
        bid.GetProperty("amount").GetInt32().Should().Be(45);
        bid.GetProperty("playerName").GetString().Should().Be(player.DisplayName);
    }
}