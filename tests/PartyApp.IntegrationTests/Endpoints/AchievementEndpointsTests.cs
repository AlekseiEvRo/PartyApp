using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.Domain.Entities;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class AchievementEndpointsTests : IClassFixture<PartyAppFactory>
{
    private const string TwoQuestionQuiz =
        """{"timeLimitSec":300,"pointsPerCorrect":10,"questions":[{"text":"Q1","options":["a","b"],"correctIndex":1},{"text":"Q2","options":["a","b"],"correctIndex":1}]}""";

    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public AchievementEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private static JsonElement Find(JsonElement items, string code)
    {
        return items.EnumerateArray().Single(i => i.GetProperty("code").GetString() == code);
    }

    private async Task<JsonElement> GetAchievementsAsync()
    {
        HttpResponseMessage response = await _api.Client.GetAsync("/api/achievements");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await PartyAppApi.ReadJsonAsync(response)).GetProperty("items");
    }

    [Fact]
    public async Task Achievements_WithoutToken_ReturnsUnauthorized()
    {
        HttpResponseMessage response = await _api.Client.GetAsync("/api/achievements");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_ReturnsCatalogWithoutAwards()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        JsonElement items = await GetAchievementsAsync();

        items.GetArrayLength().Should().BeGreaterThanOrEqualTo(5);
        items.EnumerateArray().Should().OnlyContain(i =>
            i.GetProperty("awardedAt").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task FirstSubmission_UnlocksFirstAnswerAndAwardsPoints()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        EventDefinition definition = await _factory.SeedDefinitionAsync(
            "quiz", TwoQuestionQuiz, displayName: "Квиз достижений");
        HttpResponseMessage started = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(started)).GetProperty("sessionId").GetGuid();

        _api.Authorize(player);
        HttpResponseMessage submit = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit",
            new { payloadJson = """{"questionIndex":0,"answerIndex":1}""" });
        submit.StatusCode.Should().Be(HttpStatusCode.OK);

        // 100 приветственных + 10 за квиз + 5 за достижение
        (await _api.GetBalanceAsync(player)).Should().Be(115);

        JsonElement items = await GetAchievementsAsync();
        Find(items, "first_answer").GetProperty("awardedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        Find(items, "rich_100").GetProperty("awardedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task QuizFinish_AwardsOnlyWinner()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser winner = await _api.RegisterAsync();
        TestUser loser = await _api.RegisterAsync();
        _api.Authorize(admin);

        EventDefinition definition = await _factory.SeedDefinitionAsync(
            "quiz", TwoQuestionQuiz, displayName: "Квиз победителя");
        HttpResponseMessage started = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(started)).GetProperty("sessionId").GetGuid();

        _api.Authorize(winner);
        await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = """{"questionIndex":0,"answerIndex":1}""" });
        await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = """{"questionIndex":1,"answerIndex":1}""" });

        _api.Authorize(loser);
        await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { payloadJson = """{"questionIndex":0,"answerIndex":1}""" });

        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/events/{sessionId}/finish", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        _api.Authorize(winner);
        JsonElement winnerItems = await GetAchievementsAsync();
        Find(winnerItems, "quiz_winner").GetProperty("awardedAt").ValueKind.Should().NotBe(JsonValueKind.Null);

        _api.Authorize(loser);
        JsonElement loserItems = await GetAchievementsAsync();
        Find(loserItems, "quiz_winner").GetProperty("awardedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
