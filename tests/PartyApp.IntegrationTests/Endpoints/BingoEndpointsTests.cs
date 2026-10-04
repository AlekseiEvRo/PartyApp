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

        // Отметка не начисляет баллы, но «Первый шаг» за первую отправку приходит
        _api.Authorize(player);
        HttpResponseMessage mark = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = """{"cellIndex":0}""" });
        mark.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(mark)).GetProperty("pointsAwarded").GetInt32().Should().Be(0);
        (await _api.GetBalanceAsync(player)).Should().Be(105);

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

        (await _api.GetBalanceAsync(player)).Should().Be(110); // pointsPerCell из сида + 5 за «Первый шаг»

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

    [Fact]
    public async Task MarkThenConfirm_LineBonusGoesToFastestAndLateMarksGetNothing()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser fast = await _api.RegisterAsync();
        TestUser late = await _api.RegisterAsync();

        async Task SubmitAsync(TestUser user, int cellIndex)
        {
            _api.Authorize(user);
            HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
                $"/api/events/{sessionId}/submit", new { payloadJson = $$"""{"cellIndex":{{cellIndex}}}""" });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Быстрый предсказывает первые 3 клетки (лимит конкурса — 3)
        await SubmitAsync(fast, 0);
        await SubmitAsync(fast, 1);
        await SubmitAsync(fast, 2);

        _api.Authorize(admin);
        foreach (int cell in new[] { 0, 1, 2 })
        {
            (await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/cells/{cell}/confirm", null))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Слоты освободились — быстрый дособирает линию
        await SubmitAsync(fast, 3);
        await SubmitAsync(fast, 4);

        // Медленный отмечает уже подтверждённые клетки — баллов это не приносит
        await SubmitAsync(late, 0);
        await SubmitAsync(late, 1);
        await SubmitAsync(late, 2);

        _api.Authorize(admin);
        foreach (int cell in new[] { 3, 4 })
        {
            (await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/cells/{cell}/confirm", null))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // 5 предсказаний × 5 баллов + бонус за линию 10 самому быстрому
        // + 5 за «Первый шаг» и 10 за «Линию бинго» у быстрого, + 5 за «Первый шаг» у медленного
        (await _api.GetBalanceAsync(fast)).Should().Be(150);
        (await _api.GetBalanceAsync(late)).Should().Be(105);

        _api.Authorize(admin);
        JsonElement adminState = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/bingo/{sessionId}"));
        JsonElement lineAward = adminState.GetProperty("lineAwards")[0];
        lineAward.GetProperty("playerId").GetGuid().Should().Be(fast.Id);
        lineAward.GetProperty("amount").GetInt32().Should().Be(10);
        lineAward.GetProperty("lineLabel").GetString().Should().Be("Ряд 1");
    }

    [Fact]
    public async Task AdminRejectsCell_FreesPredictionAndBlocksConfirm()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();

        _api.Authorize(player);
        (await _api.Client.PostAsJsonAsync(
                $"/api/events/{sessionId}/submit", new { payloadJson = """{"cellIndex":0}""" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(admin);
        HttpResponseMessage reject = await _api.Client.PostAsync(
            $"/api/events/bingo/{sessionId}/cells/0/reject", null);
        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(reject)).GetProperty("freedPredictions").GetInt32().Should().Be(1);

        // Отклонённую клетку нельзя подтвердить
        (await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/cells/0/confirm", null)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        // Игрок видит отклонение, слот предсказания свободен
        _api.Authorize(player);
        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("player").GetProperty("rejectedCells").EnumerateArray()
            .Select(e => e.GetInt32()).Should().Equal(0);
        data.GetProperty("player").GetProperty("pendingCount").GetInt32().Should().Be(0);
    }
}