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

    private async Task SubmitAsync(TestUser user, Guid sessionId, int cellIndex)
    {
        _api.Authorize(user);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = $$"""{"cellIndex":{{cellIndex}}}""" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> LockAsync(TestUser admin, Guid sessionId)
    {
        _api.Authorize(admin);
        return await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/lock", null);
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
    public async Task ConfirmBeforeLock_ReturnsConflict()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();
        await SubmitAsync(player, sessionId, 0);

        _api.Authorize(admin);
        HttpResponseMessage confirm = await _api.Client.PostAsync(
            $"/api/events/bingo/{sessionId}/cells/0/confirm", null);

        confirm.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(confirm)).GetProperty("error").GetString()
            .Should().Contain("приём");
    }

    [Fact]
    public async Task MarkThenLockThenAdminConfirms_AwardsPointsAndUpdatesPlayerData()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();

        // Отметка не начисляет баллы, но «Первый шаг» за первую отправку приходит
        await SubmitAsync(player, sessionId, 0);
        (await _api.GetBalanceAsync(player)).Should().Be(105);

        // До блокировки админ видит выбор и открытый приём
        _api.Authorize(admin);
        JsonElement state = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/bingo/{sessionId}"));
        state.GetProperty("markCounts").GetProperty("0").GetInt32().Should().Be(1);
        state.GetProperty("confirmedCells").GetArrayLength().Should().Be(0);
        state.GetProperty("locked").GetBoolean().Should().BeFalse();
        state.GetProperty("pickedPredictions").GetInt32().Should().Be(1);

        // Закрываем приём, затем подтверждаем событие
        (await LockAsync(admin, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage confirm = await _api.Client.PostAsync(
            $"/api/events/bingo/{sessionId}/cells/0/confirm", null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(confirm)).GetProperty("awardedPlayers").GetInt32().Should().Be(1);

        (await _api.GetBalanceAsync(player)).Should().Be(110); // pointsPerCell из сида + 5 за «Первый шаг»

        // Повторно подтвердить нельзя
        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/cells/0/confirm", null)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        // Игрок видит подтверждённую клетку и закрытый приём
        _api.Authorize(player);
        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("player").GetProperty("locked").GetBoolean().Should().BeTrue();
        data.GetProperty("player").GetProperty("confirmedCells").EnumerateArray()
            .Select(e => e.GetInt32()).Should().Equal(0);
        data.GetProperty("player").GetProperty("lines").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task MarksBeforeLock_AllConfirmedPredictionsPay_LineBonusGoesToAllCompleters()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser fast = await _api.RegisterAsync();
        TestUser late = await _api.RegisterAsync();

        // Оба собирают первую линию (5 клеток) — просто в разное время
        for (int cell = 0; cell <= 4; cell++)
            await SubmitAsync(fast, sessionId, cell);

        for (int cell = 0; cell <= 4; cell++)
            await SubmitAsync(late, sessionId, cell);

        // Пока приём открыт, игроки не получают баллов за предсказания
        (await _api.GetBalanceAsync(fast)).Should().Be(105);
        (await _api.GetBalanceAsync(late)).Should().Be(105);

        (await LockAsync(admin, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        // После блокировки все выбранные клетки подтверждаются
        _api.Authorize(admin);
        foreach (int cell in Enumerable.Range(0, 5))
        {
            (await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/cells/{cell}/confirm", null))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Каждый: 100 приветствие + 5 первый шаг + 25 за 5 предсказаний
        // + 10 бонус за линию + 10 достижение «Линия бинго» = 150
        (await _api.GetBalanceAsync(fast)).Should().Be(150);
        (await _api.GetBalanceAsync(late)).Should().Be(150);

        _api.Authorize(admin);
        JsonElement adminState = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/bingo/{sessionId}"));

        List<JsonElement> lineAwards = adminState.GetProperty("lineAwards").EnumerateArray().ToList();
        lineAwards.Should().HaveCount(2);
        lineAwards.Select(a => a.GetProperty("playerId").GetGuid())
            .Should().BeEquivalentTo(new[] { fast.Id, late.Id });
        lineAwards.Should().OnlyContain(a =>
            a.GetProperty("amount").GetInt32() == 10
            && a.GetProperty("lineLabel").GetString() == "Ряд 1");
    }

    [Fact]
    public async Task AdminRejectsCell_FixesSelectionAndBlocksConfirm()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();

        await SubmitAsync(player, sessionId, 0);
        (await LockAsync(admin, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(admin);
        HttpResponseMessage reject = await _api.Client.PostAsync(
            $"/api/events/bingo/{sessionId}/cells/0/reject", null);
        reject.StatusCode.Should().Be(HttpStatusCode.OK);

        // Отклонённую клетку нельзя подтвердить
        (await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/cells/0/confirm", null)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        // Игрок видит отклонение, но выбор зафиксирован: слот не освобождается
        _api.Authorize(player);
        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("player").GetProperty("rejectedCells").EnumerateArray()
            .Select(e => e.GetInt32()).Should().Equal(0);
        data.GetProperty("player").GetProperty("selectedCount").GetInt32().Should().Be(1);
        data.GetProperty("player").GetProperty("pendingCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task PlayerCanRemoveMarkBeforeLock()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();
        await SubmitAsync(player, sessionId, 0);

        _api.Authorize(player);
        HttpResponseMessage removed = await _api.Client.DeleteAsync($"/api/events/bingo/{sessionId}/marks/0");
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(removed)).GetProperty("selectedCount").GetInt32().Should().Be(0);

        // После снятия можно выбрать другое событие
        await SubmitAsync(player, sessionId, 1);

        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("player").GetProperty("markedCells").EnumerateArray()
            .Select(e => e.GetInt32()).Should().Equal(1);
    }

    [Fact]
    public async Task RemoveMarkAfterLock_ReturnsConflict()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();
        await SubmitAsync(player, sessionId, 0);
        (await LockAsync(admin, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(player);
        HttpResponseMessage removed = await _api.Client.DeleteAsync($"/api/events/bingo/{sessionId}/marks/0");

        removed.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task MarkAfterLock_ReturnsConflict()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();
        (await LockAsync(admin, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(player);
        HttpResponseMessage mark = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = """{"cellIndex":0}""" });

        mark.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(mark)).GetProperty("error").GetString()
            .Should().Contain("Приём предсказаний закрыт");
    }

    [Fact]
    public async Task Unlock_BeforeDecisions_ReopensAnswers()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        TestUser player = await _api.RegisterAsync();
        await SubmitAsync(player, sessionId, 0);
        (await LockAsync(admin, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(admin);
        HttpResponseMessage unlock = await _api.Client.PostAsync(
            $"/api/events/bingo/{sessionId}/unlock", null);
        unlock.StatusCode.Should().Be(HttpStatusCode.OK);

        // Приём снова открыт: игрок видит это и может менять выбор
        _api.Authorize(player);
        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("player").GetProperty("locked").GetBoolean().Should().BeFalse();

        HttpResponseMessage mark = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = """{"cellIndex":1}""" });
        mark.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unlock_AfterDecisions_ReturnsConflict()
    {
        (Guid sessionId, TestUser admin) = await StartBingoAsync();
        (await LockAsync(admin, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.Client.PostAsync($"/api/events/bingo/{sessionId}/cells/0/confirm", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        HttpResponseMessage unlock = await _api.Client.PostAsync(
            $"/api/events/bingo/{sessionId}/unlock", null);

        unlock.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
