using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

/// <summary>
/// «Шпионы»: скрытый состав, раздача слов, тайное голосование, единственная
/// попытка шпиона и закрытие без результата. Каждый тест — своя фабрика,
/// потому что сервис проверяет отсутствие активной игры.
/// </summary>
public class SpyFallEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public SpyFallEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<Guid> GetDefinitionIdAsync()
    {
        var definition = await _factory.DbAsync(db => db.EventDefinitions
            .AsNoTracking()
            .SingleAsync(d => d.Type == "spyfall"));

        return definition.Id;
    }

    private async Task<(Guid SessionId, TestUser Admin, List<TestUser> Players)> StartAsync(int playersCount = 4)
    {
        TestUser admin = await _api.CreateAdminAsync();
        Guid definitionId = await GetDefinitionIdAsync();

        var players = new List<TestUser>();
        for (int i = 0; i < playersCount; i++)
            players.Add(await _api.RegisterAsync());

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/events/spyfall/start",
            new { definitionId, playerIds = players.Select(p => p.Id).ToList() });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        Guid sessionId = (await PartyAppApi.ReadJsonAsync(response)).GetProperty("sessionId").GetGuid();
        return (sessionId, admin, players);
    }

    private async Task<JsonElement> GetAdminStateAsync(TestUser admin, Guid sessionId)
    {
        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.GetAsync($"/api/events/spyfall/{sessionId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await PartyAppApi.ReadJsonAsync(response);
    }

    private async Task<HttpResponseMessage> SubmitAsync(TestUser user, Guid sessionId, object payload)
    {
        _api.Authorize(user);
        return await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit",
            new { payloadJson = JsonSerializer.Serialize(payload) });
    }

    private static Guid FindSpyId(JsonElement state)
    {
        foreach (JsonElement participant in state.GetProperty("participants").EnumerateArray())
        {
            if (participant.GetProperty("isSpy").GetBoolean())
                return participant.GetProperty("userId").GetGuid();
        }

        throw new InvalidOperationException("В состоянии игры нет шпиона");
    }

    [Fact]
    public async Task Start_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        Guid definitionId = await GetDefinitionIdAsync();

        _api.Authorize(player);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/events/spyfall/start",
            new { definitionId, playerIds = new List<Guid> { player.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_WithThreePlayers_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        Guid definitionId = await GetDefinitionIdAsync();

        var players = new List<TestUser>();
        for (int i = 0; i < 3; i++)
            players.Add(await _api.RegisterAsync());

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/events/spyfall/start",
            new { definitionId, playerIds = players.Select(p => p.Id).ToList() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Contain("минимум 4");
    }

    [Fact]
    public async Task Start_FromGenericEndpoint_IsRejected()
    {
        TestUser admin = await _api.CreateAdminAsync();
        Guid definitionId = await GetDefinitionIdAsync();

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsync($"/api/events/{definitionId}/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Contain("вкладки");
    }

    [Fact]
    public async Task EventIsHiddenFromOutsiders_AndConfigWithWordsIsNotLeaked()
    {
        (Guid sessionId, TestUser admin, List<TestUser> players) = await StartAsync();
        TestUser outsider = await _api.RegisterAsync();

        // Участник видит ивент, посторонний — нет, админ видит всё
        _api.Authorize(players[0]);
        JsonElement participantEvents = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/available"));
        participantEvents.EnumerateArray().Select(e => e.GetProperty("sessionId").GetGuid())
            .Should().Contain(sessionId);

        _api.Authorize(outsider);
        JsonElement outsiderEvents = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/available"));
        outsiderEvents.EnumerateArray().Select(e => e.GetProperty("sessionId").GetGuid())
            .Should().NotContain(sessionId);

        _api.Authorize(admin);
        JsonElement adminEvents = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/available"));
        adminEvents.EnumerateArray().Select(e => e.GetProperty("sessionId").GetGuid())
            .Should().Contain(sessionId);

        // Данные чужому игроку недоступны
        _api.Authorize(outsider);
        HttpResponseMessage outsiderData = await _api.Client.GetAsync($"/api/events/{sessionId}/data");
        outsiderData.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // У участника нет конфига с парами слов
        _api.Authorize(players[0]);
        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("config").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("player").GetProperty("word").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("player").GetProperty("role").GetString().Should().BeOneOf("spy", "citizen");
    }

    [Fact]
    public async Task CitizensVoteForSpy_CitizensWinAndOnlyCorrectVotesGetPoints()
    {
        (Guid sessionId, TestUser admin, List<TestUser> players) = await StartAsync();
        JsonElement state = await GetAdminStateAsync(admin, sessionId);
        Guid spyId = FindSpyId(state);

        List<TestUser> citizens = players.Where(p => p.Id != spyId).ToList();

        // Первый голосует неверно, остальные — за шпиона
        (await SubmitAsync(citizens[0], sessionId, new { vote = citizens[1].Id })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await SubmitAsync(citizens[1], sessionId, new { vote = spyId })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await SubmitAsync(citizens[2], sessionId, new { vote = spyId })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // 2 голоса против 1 — горожане победили
        JsonElement resolved = await GetAdminStateAsync(admin, sessionId);
        resolved.GetProperty("winner").GetString().Should().Be("citizens");
        resolved.GetProperty("resolvedBy").GetString().Should().Be("votes");
        resolved.GetProperty("votedCount").GetInt32().Should().Be(3);

        TestUser wrongVoter = citizens[0];
        TestUser spy = players.Single(p => p.Id == spyId);

        // Верные голоса получили по 25 баллов (+105 к приветственному бонусу и достижению),
// неверный голос — только 105 за первое действие, шпион не отправлял сабмитов
        (await _api.GetBalanceAsync(citizens[1])).Should().Be(130);
        (await _api.GetBalanceAsync(citizens[2])).Should().Be(130);
        (await _api.GetBalanceAsync(wrongVoter)).Should().Be(105);
        (await _api.GetBalanceAsync(spy)).Should().Be(100);
    }

    [Fact]
    public async Task GuessWrongThenCorrect_SecondAttemptIsRejected()
    {
        (Guid sessionId, TestUser admin, List<TestUser> players) = await StartAsync();
        JsonElement state = await GetAdminStateAsync(admin, sessionId);
        Guid spyId = FindSpyId(state);
        TestUser spy = players.Single(p => p.Id == spyId);

        HttpResponseMessage wrong = await SubmitAsync(spy, sessionId, new { guess = "мимо" });
        wrong.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(wrong)).GetProperty("message").GetString()
            .Should().Contain("Попытка использована");

        // Игра продолжается: шпион ещё не победил
        JsonElement afterWrong = await GetAdminStateAsync(admin, sessionId);
        afterWrong.GetProperty("phase").GetString().Should().Be("playing");
        afterWrong.GetProperty("guessUsed").GetBoolean().Should().BeTrue();

        HttpResponseMessage second = await SubmitAsync(spy, sessionId, new { guess = "ещё раз" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(second)).GetProperty("error").GetString()
            .Should().Contain("уже использована");

        (await _api.GetBalanceAsync(spy)).Should().Be(105);
    }

    [Fact]
    public async Task GuessCorrect_SpyWinsImmediately()
    {
        (Guid sessionId, TestUser admin, List<TestUser> players) = await StartAsync();
        JsonElement state = await GetAdminStateAsync(admin, sessionId);
        Guid spyId = FindSpyId(state);
        TestUser spy = players.Single(p => p.Id == spyId);
        string citizenWord = state.GetProperty("citizenWord").GetString()!;

        HttpResponseMessage guess = await SubmitAsync(
            spy, sessionId, new { guess = citizenWord.ToUpperInvariant() });
        guess.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement guessResult = await PartyAppApi.ReadJsonAsync(guess);
        guessResult.GetProperty("message").GetString().Should().Contain("угадал");
        guessResult.GetProperty("pointsAwarded").GetInt32().Should().Be(50);

        (await _api.GetBalanceAsync(spy)).Should().Be(155);

        JsonElement resolved = await GetAdminStateAsync(admin, sessionId);
        resolved.GetProperty("winner").GetString().Should().Be("spy");
        resolved.GetProperty("resolvedBy").GetString().Should().Be("guess");

        // Игрок видит итог в личных данных
        _api.Authorize(spy);
        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("player").GetProperty("result").GetProperty("winner").GetString().Should().Be("spy");
        data.GetProperty("player").GetProperty("result").GetProperty("myPoints").GetInt32().Should().Be(50);
    }

    [Fact]
    public async Task FinishBeforeAllVotes_RevealsWithoutPoints()
    {
        (Guid sessionId, TestUser admin, List<TestUser> players) = await StartAsync();
        JsonElement state = await GetAdminStateAsync(admin, sessionId);
        Guid spyId = FindSpyId(state);

        TestUser citizen = players.First(p => p.Id != spyId);
        (await SubmitAsync(citizen, sessionId, new { vote = spyId })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        _api.Authorize(admin);
        HttpResponseMessage finish = await _api.Client.PostAsync($"/api/events/{sessionId}/finish", null);
        finish.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement closed = await GetAdminStateAsync(admin, sessionId);
        closed.GetProperty("state").GetString().Should().Be("Finished");
        closed.GetProperty("resolvedBy").GetString().Should().Be("closed");
        closed.GetProperty("winner").ValueKind.Should().Be(JsonValueKind.Null);

        // Баллы никому: только проголосовавший успел получить +5 за первое действие
        (await _api.GetBalanceAsync(citizen)).Should().Be(105);
        foreach (TestUser player in players.Where(p => p.Id != citizen.Id))
            (await _api.GetBalanceAsync(player)).Should().Be(100);

        // Завершённый ивент пропадает из списка доступных
        _api.Authorize(citizen);
        JsonElement available = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/available"));
        available.EnumerateArray().Select(e => e.GetProperty("sessionId").GetGuid())
            .Should().NotContain(sessionId);
    }

    [Fact]
    public async Task Current_WithoutGames_ReturnsNull()
    {
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.GetAsync("/api/events/spyfall/current");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task EventTypes_MarkSpyFallAsCustomStart()
    {
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(admin);
        JsonElement types = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/types"));

        JsonElement spyFall = types.EnumerateArray().Single(t => t.GetProperty("type").GetString() == "spyfall");
        spyFall.GetProperty("requiresCustomStart").GetBoolean().Should().BeTrue();
    }
}