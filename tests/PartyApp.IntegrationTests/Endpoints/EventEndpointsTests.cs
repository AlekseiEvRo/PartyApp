using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class EventEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public EventEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private async Task<Guid> StartSessionAsync(TestUser admin, Guid definitionId)
    {
        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsync($"/api/events/{definitionId}/start", null);
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        return (await PartyAppApi.ReadJsonAsync(response)).GetProperty("sessionId").GetGuid();
    }

    [Theory]
    [InlineData("/api/events/available")]
    public async Task Available_WithoutToken_ReturnsUnauthorized(string url)
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Available_ReturnsActiveSessionsForPlayers()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        EventDefinition definition = await _factory.SeedDefinitionAsync(
            "quiz", displayName: "Квиз для доступных");
        Guid sessionId = await StartSessionAsync(admin, definition.Id);

        _api.Authorize(player);
        HttpResponseMessage response = await _api.Client.GetAsync("/api/events/available");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement available = await PartyAppApi.ReadJsonAsync(response);
        JsonElement session = available.EnumerateArray()
            .Single(e => e.GetProperty("sessionId").GetGuid() == sessionId);
        session.GetProperty("definitionId").GetGuid().Should().Be(definition.Id);
        session.GetProperty("type").GetString().Should().Be("quiz");
        session.GetProperty("displayName").GetString().Should().Be("Квиз для доступных");
        session.GetProperty("availability").GetString().Should().Be("Manual");
    }

    [Fact]
    public async Task FullQuizFlow_StartSubmitReplayFinish()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        string config = """
            {
                "pointsPerCorrect": 10,
                "questions": [
                    { "text": "Столица?", "options": ["Москва", "Сочи"], "correctIndex": 0 }
                ]
            }
            """;
        EventDefinition definition = await _factory.SeedDefinitionAsync(
            "quiz", config, displayName: "Тестовый квиз");

        Guid sessionId = await StartSessionAsync(admin, definition.Id);

        // Игрок видит ивент и его конфиг
        _api.Authorize(player);
        JsonElement available = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/available"));
        available.EnumerateArray().Should().Contain(e => e.GetProperty("sessionId").GetGuid() == sessionId);

        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("type").GetString().Should().Be("quiz");
        data.GetProperty("displayName").GetString().Should().Be("Тестовый квиз");
        data.GetProperty("config").GetProperty("pointsPerCorrect").GetInt32().Should().Be(10);

        // Правильный ответ
        HttpResponseMessage submit = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit",
            new { payloadJson = """{"questionIndex":0,"answerIndex":0}""" });

        submit.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement submitJson = await PartyAppApi.ReadJsonAsync(submit);
        submitJson.GetProperty("message").GetString().Should().Be("Правильно! 🎉");
        submitJson.GetProperty("pointsAwarded").GetInt32().Should().Be(10);
        (await _api.GetBalanceAsync(player)).Should().Be(115); // 10 за ответ + 5 за «Первый шаг»

        // Повторный ответ на тот же вопрос отклоняется
        HttpResponseMessage replay = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit",
            new { payloadJson = """{"questionIndex":0,"answerIndex":0}""" });
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(replay)).GetProperty("error").GetString()
            .Should().Be("Ты уже ответил на этот вопрос");

        // Сабмит попал в БД с начисленными баллами
        PlayerSubmission[] submissions = await _factory.DbAsync(db => db.PlayerSubmissions
            .AsNoTracking()
            .Where(s => s.SessionId == sessionId)
            .ToArrayAsync());
        submissions.Should().HaveCount(2, "в БД сохраняются и успешные, и отклонённые попытки");
        submissions.Should().ContainSingle(s => s.Score == 10);

        // Админ завершает ивент
        _api.Authorize(admin);
        HttpResponseMessage finish = await _api.Client.PostAsync($"/api/events/{sessionId}/finish", null);
        finish.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage finishAgain = await _api.Client.PostAsync($"/api/events/{sessionId}/finish", null);
        finishAgain.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Ивент пропал из доступных, отправка отклоняется
        _api.Authorize(player);
        JsonElement afterFinish = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/available"));
        afterFinish.EnumerateArray().Should().NotContain(e => e.GetProperty("sessionId").GetGuid() == sessionId);

        HttpResponseMessage lateSubmit = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit",
            new { payloadJson = """{"questionIndex":0,"answerIndex":0}""" });
        lateSubmit.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(lateSubmit)).GetProperty("error").GetString()
            .Should().Be("Ивент не активен");
    }

    [Fact]
    public async Task Data_ForUnknownSession_ReturnsNotFound()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync($"/api/events/{Guid.NewGuid()}/data");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Submit_ForUnknownSession_ReturnsConflict()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            $"/api/events/{Guid.NewGuid()}/submit", new { payloadJson = "{}" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Ивент не найден");
    }

    [Fact]
    public async Task Submit_WithoutPayload_UsesEmptyJsonObject()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        EventDefinition definition = await _factory.SeedDefinitionAsync("quick_checkin");
        Guid sessionId = await StartSessionAsync(admin, definition.Id);

        _api.Authorize(player);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("pointsAwarded").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Definitions_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/events/definitions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Definitions_AsAdmin_ReturnsOnlyActiveByDefault()
    {
        TestUser admin = await _api.CreateAdminAsync();
        await _factory.SeedDefinitionAsync("quiz", isActive: false, displayName: "Выключенный квиз");
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/events/definitions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<JsonElement> definitions = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray().ToList();
        definitions.Should().NotBeEmpty();
        definitions.Should().OnlyContain(d => d.GetProperty("isActive").GetBoolean());
        definitions.Should().NotContain(d => d.GetProperty("displayName").GetString() == "Выключенный квиз");
    }

    [Fact]
    public async Task Definitions_WithIncludeInactive_ReturnsDisabledToo()
    {
        TestUser admin = await _api.CreateAdminAsync();
        await _factory.SeedDefinitionAsync("quiz", isActive: false, displayName: "Выключенный квиз 2");
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/events/definitions?includeInactive=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<JsonElement> definitions = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray().ToList();
        definitions.Should().Contain(d => d.GetProperty("displayName").GetString() == "Выключенный квиз 2");
    }

    [Fact]
    public async Task Types_AsAdmin_ReturnsEveryHandlerWithValidDefaultConfig()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/events/types");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<JsonElement> types = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray().ToList();

        types.Select(t => t.GetProperty("type").GetString())
            .Should().BeEquivalentTo(
                "quiz", "word_rush", "promo_code", "qr_scan", "quick_checkin",
                "reaction", "dare", "bingo", "emoji_song", "predictions", "raffle");

        foreach (JsonElement type in types)
        {
            string defaultConfig = type.GetProperty("defaultConfigJson").GetString()!;
            JsonDocument.Parse(defaultConfig).RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }
    }

    [Fact]
    public async Task Types_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/events/types");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        EventDefinition definition = await _factory.SeedDefinitionAsync("quiz");
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_WithUnknownDefinition_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync($"/api/events/{Guid.NewGuid()}/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Start_InactiveDefinition_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        EventDefinition definition = await _factory.SeedDefinitionAsync("quiz", isActive: false);
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Contain("not active");
    }

    [Fact]
    public async Task Start_WithoutRegisteredHandler_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        EventDefinition definition = await _factory.SeedDefinitionAsync("type_without_handler");
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Contain("No handler registered");
    }

    [Fact]
    public async Task Finish_WithUnknownSession_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync($"/api/events/{Guid.NewGuid()}/finish", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}