using System.Net;

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
}