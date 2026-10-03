using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class PartyEndpointsTests : IClassFixture<PartyAppFactory>
{
    private const string QuizConfig =
        """{"timeLimitSec":30,"pointsPerCorrect":10,"questions":[{"text":"Q","options":["a","b"],"correctIndex":1}]}""";

    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public PartyEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private Task<HttpResponseMessage> StartPartyAsync(string name, bool resetBalances = true)
    {
        return _api.Client.PostAsJsonAsync("/api/parties", new { name, resetBalances });
    }

    [Fact]
    public async Task Current_BeforeFirstParty_ReturnsNullSummary()
    {
        using PartyAppFactory factory = new();
        PartyAppApi api = new(factory);
        TestUser player = await api.RegisterAsync();
        api.Authorize(player);

        HttpResponseMessage response = await api.Client.GetAsync("/api/parties/current");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("summary").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("nextScheduleItem").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Parties_AsPlayer_ReturnsForbidden()
    {
        using PartyAppFactory factory = new();
        PartyAppApi api = new(factory);
        TestUser player = await api.RegisterAsync();
        api.Authorize(player);

        (await api.Client.GetAsync("/api/parties")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await api.Client.PostAsJsonAsync("/api/parties", new { name = "Взлом" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_CreatesPartyAndResetsBalances()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await StartPartyAsync("Сброс баллов");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement party = await PartyAppApi.ReadJsonAsync(response);
        party.GetProperty("status").GetString().Should().Be("Active");

        (await _api.GetBalanceAsync(player)).Should().Be(0);

        await _factory.DbAsync(async db =>
        {
            WalletTransaction reset = await db.WalletTransactions
                .SingleAsync(t => t.Wallet.UserId == player.Id && t.Type == WalletTransactionType.PartyReset);
            reset.Amount.Should().Be(-100);
            reset.Description.Should().Be("Новая вечеринка");

            AdminAuditLog log = await db.AdminAuditLogs
                .Where(a => a.Action == "party_start")
                .OrderByDescending(a => a.CreatedAt)
                .FirstAsync();
            log.AdminId.Should().Be(admin.Id);
            log.Details.Should().Be("Сброс баллов · баллы обнулены");
        });
    }

    [Fact]
    public async Task Start_WithoutReset_KeepsBalances()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        (await StartPartyAsync("Без сброса", resetBalances: false)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await _api.GetBalanceAsync(player)).Should().Be(100);
    }

    [Fact]
    public async Task Start_FinishesPreviousPartyAndClosesActiveEvents()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        (await StartPartyAsync("Первая")).StatusCode.Should().Be(HttpStatusCode.OK);

        EventDefinition definition = await _factory.SeedDefinitionAsync(
            "quiz", QuizConfig, displayName: "Квиз до смены");
        HttpResponseMessage started = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(started)).GetProperty("sessionId").GetGuid();

        (await StartPartyAsync("Вторая")).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement sessions = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync("/api/admin/sessions"));
        JsonElement session = sessions.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == sessionId);
        session.GetProperty("state").GetString().Should().Be("Finished");

        JsonElement parties = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync("/api/parties"));
        parties.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Первая")
            .GetProperty("status").GetString().Should().Be("Finished");
        parties.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Вторая")
            .GetProperty("status").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task Summary_CountsContentAndEarnings()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        JsonElement party = await PartyAppApi.ReadJsonAsync(await StartPartyAsync("Итоги"));
        Guid partyId = party.GetProperty("id").GetGuid();

        EventDefinition definition = await _factory.SeedDefinitionAsync(
            "quiz", QuizConfig, displayName: "Квиз итогов");
        HttpResponseMessage started = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        Guid sessionId = (await PartyAppApi.ReadJsonAsync(started)).GetProperty("sessionId").GetGuid();

        _api.Authorize(player);
        HttpResponseMessage submit = await _api.Client.PostAsJsonAsync(
            $"/api/events/{sessionId}/submit",
            new { payloadJson = """{"questionIndex":0,"answerIndex":1}""" });
        submit.StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.GetAsync($"/api/parties/{partyId}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement summary = await PartyAppApi.ReadJsonAsync(response);
        summary.GetProperty("party").GetProperty("id").GetGuid().Should().Be(partyId);
        summary.GetProperty("eventsCount").GetInt32().Should().Be(1);
        summary.GetProperty("eventsByType").GetProperty("quiz").GetInt32().Should().Be(1);
        summary.GetProperty("submissionsCount").GetInt32().Should().Be(1);
        summary.GetProperty("playersCount").GetInt32().Should().Be(1);

        JsonElement topPlayer = summary.GetProperty("topPlayers")[0];
        topPlayer.GetProperty("playerId").GetGuid().Should().Be(player.Id);
        topPlayer.GetProperty("earned").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Finish_ReturnsSummaryAndIsIdempotent()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        Guid partyId = (await PartyAppApi.ReadJsonAsync(await StartPartyAsync("Финиш"))).GetProperty("id").GetGuid();

        HttpResponseMessage first = await _api.Client.PostAsync($"/api/parties/{partyId}/finish", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement summary = await PartyAppApi.ReadJsonAsync(first);
        summary.GetProperty("party").GetProperty("status").GetString().Should().Be("Finished");

        HttpResponseMessage second = await _api.Client.PostAsync($"/api/parties/{partyId}/finish", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(second)).GetProperty("party").GetProperty("status").GetString()
            .Should().Be("Finished");
    }

    [Fact]
    public async Task Finish_UnknownParty_ReturnsNotFound()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        (await _api.Client.PostAsync($"/api/parties/{Guid.NewGuid()}/finish", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Schedule_AddMoveStartDelete()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        Guid partyId = (await PartyAppApi.ReadJsonAsync(await StartPartyAsync("Сценарий"))).GetProperty("id").GetGuid();

        EventDefinition first = await _factory.SeedDefinitionAsync(
            "quiz", QuizConfig, displayName: "Первый по плану");
        EventDefinition second = await _factory.SeedDefinitionAsync(
            "raffle", """{"prize":"Приз"}""", displayName: "Второй по плану");

        JsonElement firstItem = await PartyAppApi.ReadJsonAsync(
            await _api.Client.PostAsJsonAsync($"/api/parties/{partyId}/schedule", new { definitionId = first.Id }));
        JsonElement secondItem = await PartyAppApi.ReadJsonAsync(
            await _api.Client.PostAsJsonAsync($"/api/parties/{partyId}/schedule", new { definitionId = second.Id }));

        JsonElement schedule = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/parties/{partyId}/schedule"));
        schedule.EnumerateArray().Select(i => i.GetProperty("displayName").GetString())
            .Should().Equal("Первый по плану", "Второй по плану");

        Guid secondItemId = secondItem.GetProperty("id").GetGuid();
        (await _api.Client.PostAsJsonAsync(
            $"/api/parties/{partyId}/schedule/{secondItemId}/move", new { up = true })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        schedule = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync($"/api/parties/{partyId}/schedule"));
        schedule.EnumerateArray().Select(i => i.GetProperty("displayName").GetString())
            .Should().Equal("Второй по плану", "Первый по плану");

        HttpResponseMessage start = await _api.Client.PostAsync(
            $"/api/parties/{partyId}/schedule/{secondItemId}/start", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(start)).GetProperty("sessionId").GetGuid().Should().NotBeEmpty();

        // Запущенный пункт пропускается: «далее» — первый
        JsonElement current = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync("/api/parties/current"));
        current.GetProperty("nextScheduleItem").GetProperty("id").GetGuid()
            .Should().Be(firstItem.GetProperty("id").GetGuid());

        (await _api.Client.DeleteAsync($"/api/parties/{partyId}/schedule/{firstItem.GetProperty("id").GetGuid()}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        schedule = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync($"/api/parties/{partyId}/schedule"));
        schedule.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Schedule_AddToFinishedParty_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        Guid partyId = (await PartyAppApi.ReadJsonAsync(await StartPartyAsync("Завершённая"))).GetProperty("id").GetGuid();
        (await _api.Client.PostAsync($"/api/parties/{partyId}/finish", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        EventDefinition definition = await _factory.SeedDefinitionAsync("quiz", QuizConfig, displayName: "Поздний");

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            $"/api/parties/{partyId}/schedule", new { definitionId = definition.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Parties_AsAdmin_ReturnsCounts()
    {
        using PartyAppFactory factory = new();
        PartyAppApi api = new(factory);
        TestUser admin = await api.CreateAdminAsync();
        api.Authorize(admin);

        Guid partyId = (await PartyAppApi.ReadJsonAsync(
            await api.Client.PostAsJsonAsync("/api/parties", new { name = "Со счётчиками" }))).GetProperty("id").GetGuid();

        EventDefinition definition = await factory.SeedDefinitionAsync("quiz", QuizConfig, displayName: "Счётный");
        (await api.Client.PostAsync($"/api/events/{definition.Id}/start", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        JsonElement parties = await PartyAppApi.ReadJsonAsync(await api.Client.GetAsync("/api/parties"));
        JsonElement party = parties.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == partyId);
        party.GetProperty("sessionsCount").GetInt32().Should().Be(1);
        party.GetProperty("photosCount").GetInt32().Should().Be(0);
    }
}
