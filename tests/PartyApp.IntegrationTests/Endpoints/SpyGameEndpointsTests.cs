using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

/// <summary>
/// SpyGameService — singleton с состоянием в памяти, поэтому каждый тест
/// работает со свежим приложением.
/// </summary>
public class SpyGameEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public SpyGameEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<List<(TestUser User, PartyAppApi Api)>> CreatePlayersAsync(int count)
    {
        List<(TestUser, PartyAppApi)> players = new();
        for (int i = 0; i < count; i++)
        {
            PartyAppApi api = new(_factory);
            TestUser user = await api.RegisterAsync();
            api.Authorize(user);
            players.Add((user, api));
        }

        return players;
    }

    private async Task<JsonElement> StartGameAsync(TestUser admin, IEnumerable<TestUser> players)
    {
        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/spygame/start",
            new { playerIds = players.Select(p => p.Id).ToList() });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        return await PartyAppApi.ReadJsonAsync(response);
    }

    private async Task<JsonElement> GetStateAsync(TestUser admin)
    {
        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.GetAsync("/api/spygame/state");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await PartyAppApi.ReadJsonAsync(response);
    }

    private static bool IsSpy(JsonElement role) => role.GetProperty("role").GetString() == "Spy";

    private static bool HasSecretWord(JsonElement role) =>
        role.GetProperty("secretWord").ValueKind != JsonValueKind.Null;

    private async Task<Dictionary<Guid, JsonElement>> GetRolesAsync(
        IEnumerable<(TestUser User, PartyAppApi Api)> players)
    {
        Dictionary<Guid, JsonElement> roles = new();
        foreach ((TestUser user, PartyAppApi api) in players)
        {
            HttpResponseMessage response = await api.Client.GetAsync("/api/spygame/my-role");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            roles[user.Id] = await PartyAppApi.ReadJsonAsync(response);
        }

        return roles;
    }

    [Fact]
    public async Task Start_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/spygame/start", new { playerIds = new[] { player.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_WithThreePlayers_ReturnsServerError()
    {
        // Известное ограничение: InvalidOperationException из сервиса
        // не обрабатывается эндпоинтом и превращается в 500.
        TestUser admin = await _api.CreateAdminAsync();
        List<(TestUser User, PartyAppApi Api)> players = await CreatePlayersAsync(3);

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/spygame/start", new { playerIds = players.Select(p => p.User.Id).ToList() });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Start_WithFourPlayers_AssignsTwoSpies()
    {
        TestUser admin = await _api.CreateAdminAsync();
        List<(TestUser User, PartyAppApi Api)> players = await CreatePlayersAsync(4);

        JsonElement start = await StartGameAsync(admin, players.Select(p => p.User));

        start.GetProperty("sessionId").GetGuid().Should().NotBe(Guid.Empty);
        start.GetProperty("playersCount").GetInt32().Should().Be(4);
        start.GetProperty("secretWord").GetString().Should().NotBeNullOrWhiteSpace();

        JsonElement state = await GetStateAsync(admin);
        state.GetProperty("phase").GetString().Should().Be("Playing");
        state.GetProperty("sessionId").GetGuid().Should().Be(start.GetProperty("sessionId").GetGuid());
        List<JsonElement> statePlayers = state.GetProperty("players").EnumerateArray().ToList();
        statePlayers.Should().HaveCount(4);
        statePlayers.Count(IsSpy).Should().Be(2);
        statePlayers.Should().OnlyContain(p => !p.GetProperty("hasAccused").GetBoolean());
    }

    [Fact]
    public async Task MyRole_SplitsSpiesIntoNarratorAndGuesser()
    {
        TestUser admin = await _api.CreateAdminAsync();
        List<(TestUser User, PartyAppApi Api)> players = await CreatePlayersAsync(4);
        JsonElement start = await StartGameAsync(admin, players.Select(p => p.User));

        Dictionary<Guid, JsonElement> roles = await GetRolesAsync(players);

        roles.Values.Count(IsSpy).Should().Be(2);
        Guid narratorId = roles.Single(r => IsSpy(r.Value) && HasSecretWord(r.Value)).Key;
        Guid guesserId = roles.Single(r => IsSpy(r.Value) && !HasSecretWord(r.Value)).Key;
        JsonElement narrator = roles[narratorId];
        JsonElement guesser = roles[guesserId];
        string narratorName = players.Single(p => p.User.Id == narratorId).User.DisplayName;
        string guesserName = players.Single(p => p.User.Id == guesserId).User.DisplayName;

        narrator.GetProperty("secretWord").GetString().Should().Be(start.GetProperty("secretWord").GetString());
        narrator.GetProperty("isGuesser").GetBoolean().Should().BeFalse();
        guesser.GetProperty("isGuesser").GetBoolean().Should().BeTrue();
        narrator.GetProperty("partnerName").GetString().Should().Be(guesserName);
        guesser.GetProperty("partnerName").GetString().Should().Be(narratorName);
        roles.Values.Should().OnlyContain(r => r.GetProperty("allPlayers").GetArrayLength() == 3);
    }

    [Fact]
    public async Task MyRole_BeforeGameStart_ReturnsNotFound()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/spygame/my-role");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MyRole_ForNonParticipant_ReturnsNotFound()
    {
        TestUser admin = await _api.CreateAdminAsync();
        List<(TestUser User, PartyAppApi Api)> players = await CreatePlayersAsync(4);
        await StartGameAsync(admin, players.Select(p => p.User));

        PartyAppApi strangerApi = new(_factory);
        TestUser stranger = await strangerApi.RegisterAsync();
        strangerApi.Authorize(stranger);

        HttpResponseMessage response = await strangerApi.Client.GetAsync("/api/spygame/my-role");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SubmitWord_GuesserWinsForSpies()
    {
        TestUser admin = await _api.CreateAdminAsync();
        List<(TestUser User, PartyAppApi Api)> players = await CreatePlayersAsync(4);
        await StartGameAsync(admin, players.Select(p => p.User));
        Dictionary<Guid, JsonElement> roles = await GetRolesAsync(players);

        (TestUser narratorUser, PartyAppApi narratorApi) = players.Single(p => IsSpy(roles[p.User.Id]) && HasSecretWord(roles[p.User.Id]));
        (TestUser guesserUser, PartyAppApi guesserApi) = players.Single(p => IsSpy(roles[p.User.Id]) && !HasSecretWord(roles[p.User.Id]));
        (TestUser townUser, PartyAppApi townApi) = players.First(p => !IsSpy(roles[p.User.Id]));
        string word = roles[narratorUser.Id].GetProperty("secretWord").GetString()!;

        // Горожанин не может вводить слово
        HttpResponseMessage townAttempt = await townApi.Client.PostAsJsonAsync(
            "/api/spygame/submit-word", new { word });
        townAttempt.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(townAttempt)).GetProperty("error").GetString()
            .Should().Be("Только шпионы могут вводить слово");

        // Рассказчик не угадывает
        HttpResponseMessage narratorAttempt = await narratorApi.Client.PostAsJsonAsync(
            "/api/spygame/submit-word", new { word });
        narratorAttempt.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(narratorAttempt)).GetProperty("error").GetString()
            .Should().Contain("Ты рассказчик");

        // Неверное слово
        HttpResponseMessage wrongAttempt = await guesserApi.Client.PostAsJsonAsync(
            "/api/spygame/submit-word", new { word = "заведомо-неверное-слово" });
        wrongAttempt.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Верное слово — победа шпионов
        HttpResponseMessage win = await guesserApi.Client.PostAsJsonAsync(
            "/api/spygame/submit-word", new { word = word.ToUpperInvariant() });
        win.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement winJson = await PartyAppApi.ReadJsonAsync(win);
        winJson.GetProperty("message").GetString().Should().Be("Шпионы победили!");
        winJson.GetProperty("pointsAwarded").GetInt32().Should().Be(50);
        winJson.GetProperty("data").GetProperty("winner").GetString().Should().Be("spies");

        JsonElement state = await GetStateAsync(admin);
        state.GetProperty("phase").GetString().Should().Be("Finished");
        state.GetProperty("winner").GetString().Should().Be("spies");

        (await _api.GetBalanceAsync(narratorUser)).Should().Be(150);
        (await _api.GetBalanceAsync(guesserUser)).Should().Be(150);
        (await _api.GetBalanceAsync(townUser)).Should().Be(100);

        // После завершения роль больше не отдаётся
        HttpResponseMessage afterFinish = await guesserApi.Client.GetAsync("/api/spygame/my-role");
        afterFinish.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accuse_CorrectSpyWinsForTown()
    {
        TestUser admin = await _api.CreateAdminAsync();
        List<(TestUser User, PartyAppApi Api)> players = await CreatePlayersAsync(4);
        await StartGameAsync(admin, players.Select(p => p.User));
        Dictionary<Guid, JsonElement> roles = await GetRolesAsync(players);

        (TestUser spyUser, _) = players.First(p => IsSpy(roles[p.User.Id]));
        List<(TestUser User, PartyAppApi Api)> townsfolk = players.Where(p => !IsSpy(roles[p.User.Id])).ToList();

        // Первый горожанин ошибается и тратит попытку
        HttpResponseMessage wrong = await townsfolk[0].Api.Client.PostAsJsonAsync(
            "/api/spygame/accuse", new { accusedPlayerId = townsfolk[1].User.Id });
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(wrong)).GetProperty("error").GetString()
            .Should().Contain("не шпион");

        HttpResponseMessage repeat = await townsfolk[0].Api.Client.PostAsJsonAsync(
            "/api/spygame/accuse", new { accusedPlayerId = spyUser.Id });
        repeat.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(repeat)).GetProperty("error").GetString()
            .Should().Be("Ты уже делал обвинение");

        // Второй горожанин разоблачает шпиона
        HttpResponseMessage win = await townsfolk[1].Api.Client.PostAsJsonAsync(
            "/api/spygame/accuse", new { accusedPlayerId = spyUser.Id });
        win.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement winJson = await PartyAppApi.ReadJsonAsync(win);
        winJson.GetProperty("pointsAwarded").GetInt32().Should().Be(50);
        winJson.GetProperty("data").GetProperty("winner").GetString().Should().Be("town");

        JsonElement state = await GetStateAsync(admin);
        state.GetProperty("winner").GetString().Should().Be("town");
        state.GetProperty("townWinnerName").GetString().Should().Be(townsfolk[1].User.DisplayName);
        (await _api.GetBalanceAsync(townsfolk[1].User)).Should().Be(150);
    }

    [Fact]
    public async Task Finish_WhenIdle_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync("/api/spygame/finish", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Игра не запущена");
    }

    [Fact]
    public async Task Finish_WhilePlaying_EndsInDraw()
    {
        TestUser admin = await _api.CreateAdminAsync();
        List<(TestUser User, PartyAppApi Api)> players = await CreatePlayersAsync(4);
        await StartGameAsync(admin, players.Select(p => p.User));

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsync("/api/spygame/finish", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("winner").GetString().Should().Be("draw");
        json.GetProperty("pointsAwarded").GetInt32().Should().Be(0);

        JsonElement state = await GetStateAsync(admin);
        state.GetProperty("phase").GetString().Should().Be("Finished");
    }

    [Fact]
    public async Task State_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/spygame/state");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}