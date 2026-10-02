using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class BingoEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public BingoEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<(Guid SessionId, TestUser Admin)> StartBingoAsync()
    {
        TestUser admin = await _api.CreateAdminAsync();
        var definition = await _factory.DbAsync(db => db.EventDefinitions
            .AsNoTracking()
            .SingleAsync(d => d.Type == "bingo"));

        _api.Authorize(admin);
        HttpResponseMessage start = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);

        Guid sessionId = (await PartyAppApi.ReadJsonAsync(start)).GetProperty("sessionId").GetGuid();
        return (sessionId, admin);
    }

    [Fact]
    public async Task AdminState_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync($"/api/events/bingo/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MarkThenAdminConfirms_AwardsPointsAndUpdatesPlayerData()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();

        // Отметка не начисляет баллы
        _api.Authorize(player);
        HttpResponseMessage mark = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = """{"cellIndex":0}""" });
        mark.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(mark)).GetProperty("pointsAwarded").GetInt32().Should().Be(0);
        (await _api.GetBalanceAsync(player)).Should().Be(100);

        // Админ видит отметку и подтверждает событие
        _api.Authorize(admin);
        JsonElement state = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/bingo/{sessionId}"));
        state.GetProperty("markCounts").GetProperty("0").GetInt32().Should().Be(1);
        state.GetProperty("confirmedCells").GetArrayLength().Should().Be(0);

        HttpResponseMessage confirm = await _api.Client.PostAsync(
            $"/api/events/bingo/{sessionId}/cells/0/confirm", null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(confirm)).GetProperty("awardedPlayers").GetInt32().Should().Be(1);

        (await _api.GetBalanceAsync(player)).Should().Be(101); // pointsPerCell из сида

        // Повторно подтвердить нельзя
        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/cells/0/confirm", null)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        // Игрок видит подтверждённую клетку
        _api.Authorize(player);
        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("player").GetProperty("confirmedCells").EnumerateArray()
            .Select(e => e.GetInt32()).Should().Equal(0);
        data.GetProperty("player").GetProperty("lines").GetInt32().Should().Be(0);
    }
}