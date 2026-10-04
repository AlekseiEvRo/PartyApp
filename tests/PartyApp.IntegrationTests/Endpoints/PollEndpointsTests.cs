using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.Domain.Entities;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class PollEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public PollEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private static Guid OptionFor(JsonElement poll, Guid definitionId)
    {
        return poll.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("definitionId").GetGuid() == definitionId)
            .GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Current_WithoutPoll_ReturnsNull()
    {
        using PartyAppFactory factory = new();
        PartyAppApi api = new(factory);
        TestUser player = await api.RegisterAsync();
        api.Authorize(player);

        HttpResponseMessage response = await api.Client.GetAsync("/api/polls/current");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("poll").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Create_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/polls", new { question = "Что дальше?", definitionIds = Array.Empty<Guid>() });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_ValidatesInput()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);
        EventDefinition definition = await _factory.SeedDefinitionAsync("quiz", "{}", displayName: "Один вариант");

        HttpResponseMessage emptyQuestion = await _api.Client.PostAsJsonAsync(
            "/api/polls", new { question = "  ", definitionIds = new[] { definition.Id, definition.Id } });
        emptyQuestion.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        HttpResponseMessage fewOptions = await _api.Client.PostAsJsonAsync(
            "/api/polls", new { question = "Что дальше?", definitionIds = new[] { definition.Id } });
        fewOptions.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        HttpResponseMessage unknown = await _api.Client.PostAsJsonAsync(
            "/api/polls", new { question = "Что дальше?", definitionIds = new[] { definition.Id, Guid.NewGuid() } });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FullFlow_VoteCloseStartWinner()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser alice = await _api.RegisterAsync();
        TestUser bob = await _api.RegisterAsync();
        _api.Authorize(admin);

        EventDefinition quiz = await _factory.SeedDefinitionAsync(
            "quiz", """{"timeLimitSec":30,"pointsPerCorrect":10,"questions":[]}""",
            displayName: "Квиз голосования");
        EventDefinition raffle = await _factory.SeedDefinitionAsync(
            "raffle", """{"prize":"Приз"}""",
            displayName: "Лототрон голосования");

        HttpResponseMessage created = await _api.Client.PostAsJsonAsync(
            "/api/polls", new { question = "Что дальше?", definitionIds = new[] { quiz.Id, raffle.Id } });
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement poll = await PartyAppApi.ReadJsonAsync(created);
        Guid pollId = poll.GetProperty("id").GetGuid();
        Guid quizOption = OptionFor(poll, quiz.Id);
        Guid raffleOption = OptionFor(poll, raffle.Id);

        // Алиса голосует за квиз, Боб сначала за лототрон, потом передумывает
        _api.Authorize(alice);
        HttpResponseMessage aliceVote = await _api.Client.PostAsJsonAsync(
            $"/api/polls/{pollId}/vote", new { optionId = quizOption });
        aliceVote.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(aliceVote)).GetProperty("myOptionId").GetGuid().Should().Be(quizOption);

        _api.Authorize(bob);
        await _api.Client.PostAsJsonAsync($"/api/polls/{pollId}/vote", new { optionId = raffleOption });
        HttpResponseMessage revote = await _api.Client.PostAsJsonAsync(
            $"/api/polls/{pollId}/vote", new { optionId = quizOption });
        revote.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement updated = await PartyAppApi.ReadJsonAsync(revote);
        updated.GetProperty("totalVotes").GetInt32().Should().Be(2);
        OptionFor(updated, quiz.Id).Should().NotBeEmpty();
        updated.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("definitionId").GetGuid() == quiz.Id)
            .GetProperty("votes").GetInt32().Should().Be(2);
        updated.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("definitionId").GetGuid() == raffle.Id)
            .GetProperty("votes").GetInt32().Should().Be(0);

        _api.Authorize(admin);
        HttpResponseMessage close = await _api.Client.PostAsync($"/api/polls/{pollId}/close", null);
        close.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement closed = await PartyAppApi.ReadJsonAsync(close);
        closed.GetProperty("status").GetString().Should().Be("Closed");
        closed.GetProperty("winnerOptionId").GetGuid().Should().Be(quizOption);

        HttpResponseMessage start = await _api.Client.PostAsync($"/api/polls/{pollId}/start-winner", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(start)).GetProperty("sessionId").GetGuid();
        sessionId.Should().NotBeEmpty();

        JsonElement available = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/available"));
        available.EnumerateArray().Should().Contain(e => e.GetProperty("sessionId").GetGuid() == sessionId);
    }

    [Fact]
    public async Task Vote_AfterClose_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        EventDefinition first = await _factory.SeedDefinitionAsync("quiz", "{}", displayName: "Первый вариант");
        EventDefinition second = await _factory.SeedDefinitionAsync("raffle", "{}", displayName: "Второй вариант");

        JsonElement poll = await PartyAppApi.ReadJsonAsync(await _api.Client.PostAsJsonAsync(
            "/api/polls", new { question = "Что дальше?", definitionIds = new[] { first.Id, second.Id } }));
        Guid pollId = poll.GetProperty("id").GetGuid();

        (await _api.Client.PostAsync($"/api/polls/{pollId}/close", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        _api.Authorize(player);
        HttpResponseMessage vote = await _api.Client.PostAsJsonAsync(
            $"/api/polls/{pollId}/vote", new { optionId = OptionFor(poll, first.Id) });

        vote.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(vote)).GetProperty("error").GetString()
            .Should().Be("Голосование уже закрыто");
    }

    [Fact]
    public async Task StartWinner_WithoutVotes_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        EventDefinition first = await _factory.SeedDefinitionAsync("quiz", "{}", displayName: "Без голосов 1");
        EventDefinition second = await _factory.SeedDefinitionAsync("raffle", "{}", displayName: "Без голосов 2");

        JsonElement poll = await PartyAppApi.ReadJsonAsync(await _api.Client.PostAsJsonAsync(
            "/api/polls", new { question = "Что дальше?", definitionIds = new[] { first.Id, second.Id } }));

        HttpResponseMessage start = await _api.Client.PostAsync(
            $"/api/polls/{poll.GetProperty("id").GetGuid()}/start-winner", null);

        start.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(start)).GetProperty("error").GetString()
            .Should().Be("В голосовании пока нет голосов");
    }
}
