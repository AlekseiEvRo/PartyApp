using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events;

public class EventServiceTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly IEventHandlerFactory _factory = Substitute.For<IEventHandlerFactory>();
    private readonly IEventHandler _handler = Substitute.For<IEventHandler>();
    private readonly RecordingHubContext _hub = new();
    private readonly FakePushNotificationService _push = new();
    private readonly EventService _service;
    private readonly Guid _adminId = Guid.NewGuid();

    public EventServiceTests()
    {
        _host = new SqliteTestHost();
        _service = new EventService(
            _host.ScopeFactory,
            _factory,
            _hub,
            _push,
            NullLogger<EventService>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private async Task<User> SeedAdminAsync()
    {
        User admin = TestData.User("admin", UserRole.Admin);
        admin.Id = _adminId;
        _host.Db.Users.Add(admin);
        await _host.Db.SaveChangesAsync();
        return admin;
    }

    private async Task<EventDefinition> SeedDefinitionAsync(
        string type = "quiz",
        string configJson = "{}",
        bool isActive = true,
        string? displayName = null)
    {
        EventDefinition definition = TestData.Definition(type, configJson, isActive, displayName);
        _host.Db.EventDefinitions.Add(definition);
        await _host.Db.SaveChangesAsync();
        return definition;
    }

    private async Task<EventSession> SeedSessionAsync(
        EventDefinition definition,
        EventSessionState state = EventSessionState.Active)
    {
        EventSession session = TestData.Session(definition, _adminId, state);
        session.Definition = null!;
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();
        return session;
    }

    private static JsonElement Json(object? value)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;
    }

    // === GetDefinitionsAsync ===

    [Fact]
    public async Task GetDefinitionsAsync_ReturnsOnlyActiveOrderedByCreatedAt()
    {
        EventDefinition first = TestData.Definition("quiz", displayName: "Первый");
        first.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        EventDefinition second = TestData.Definition("word_rush", displayName: "Второй");
        second.CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        EventDefinition inactive = TestData.Definition("promo_code", isActive: false, displayName: "Выключенный");
        inactive.CreatedAt = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        _host.Db.EventDefinitions.AddRange(second, inactive, first);
        await _host.Db.SaveChangesAsync();

        List<EventDefinition> definitions = await _service.GetDefinitionsAsync();

        definitions.Select(d => d.DisplayName).Should().Equal("Первый", "Второй");
    }

    [Fact]
    public async Task GetDefinitionsAsync_WithIncludeInactive_ReturnsEverything()
    {
        _host.Db.EventDefinitions.AddRange(
            TestData.Definition("quiz", isActive: true),
            TestData.Definition("word_rush", isActive: false));
        await _host.Db.SaveChangesAsync();

        List<EventDefinition> definitions = await _service.GetDefinitionsAsync(includeInactive: true);

        definitions.Should().HaveCount(2);
    }

    // === GetAvailableEventsAsync ===

    [Fact]
    public async Task GetAvailableEventsAsync_ReturnsOnlyActiveSessionsOfActiveDefinitions()
    {
        await SeedAdminAsync();
        EventDefinition activeDefinition = await SeedDefinitionAsync("quiz", displayName: "Активный квиз");
        EventDefinition inactiveDefinition = await SeedDefinitionAsync("word_rush", isActive: false);

        await SeedSessionAsync(activeDefinition, EventSessionState.Active);
        await SeedSessionAsync(activeDefinition, EventSessionState.Finished);
        await SeedSessionAsync(inactiveDefinition, EventSessionState.Active);

        List<AvailableEventDto> events = await _service.GetAvailableEventsAsync();

        AvailableEventDto available = events.Should().ContainSingle().Subject;
        available.DefinitionId.Should().Be(activeDefinition.Id);
        available.Type.Should().Be("quiz");
        available.DisplayName.Should().Be("Активный квиз");
        available.Availability.Should().Be(nameof(AvailabilityMode.Manual));
        available.SessionId.Should().NotBe(Guid.Empty);
    }

    // === StartEventAsync ===

    [Fact]
    public async Task StartEventAsync_WithUnknownDefinition_Throws()
    {
        Guid unknownId = Guid.NewGuid();

        Func<Task> act = () => _service.StartEventAsync(unknownId, _adminId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{unknownId}*");
    }

    [Fact]
    public async Task StartEventAsync_WithInactiveDefinition_Throws()
    {
        EventDefinition definition = await SeedDefinitionAsync(isActive: false);

        Func<Task> act = () => _service.StartEventAsync(definition.Id, _adminId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not active*");
    }

    [Fact]
    public async Task StartEventAsync_WithoutRegisteredHandler_Throws()
    {
        EventDefinition definition = await SeedDefinitionAsync(type: "unknown_type");
        _factory.HasHandler("unknown_type").Returns(false);

        Func<Task> act = () => _service.StartEventAsync(definition.Id, _adminId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No handler registered*");
    }

    [Fact]
    public async Task StartEventAsync_CreatesSessionNotifiesHandlerHubAndPush()
    {
        User admin = await SeedAdminAsync();
        EventDefinition definition = await SeedDefinitionAsync(displayName: "Квиз про именинника");
        _factory.HasHandler("quiz").Returns(true);
        _factory.GetHandler("quiz").Returns(_handler);

        EventSession session = await _service.StartEventAsync(definition.Id, admin.Id);

        session.State.Should().Be(EventSessionState.Active);
        session.StartedById.Should().Be(admin.Id);
        session.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        EventSession? stored = await _host.DbAsync(db => db.EventSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == session.Id));
        stored.Should().NotBeNull();
        stored!.State.Should().Be(EventSessionState.Active);

        await _handler.Received(1).OnSessionStartedAsync(
            Arg.Is<EventSession>(s => s.Id == session.Id),
            Arg.Is<EventDefinition>(d => d.Id == definition.Id),
            Arg.Any<CancellationToken>());

        RecordingHubContext.HubCall hubCall = _hub.SingleCall("EventStarted");
        hubCall.Target.Should().Be("all");
        JsonElement payload = Json(hubCall.Payload);
        payload.GetProperty("sessionId").GetGuid().Should().Be(session.Id);
        payload.GetProperty("definitionId").GetGuid().Should().Be(definition.Id);
        payload.GetProperty("type").GetString().Should().Be("quiz");
        payload.GetProperty("displayName").GetString().Should().Be("Квиз про именинника");
        payload.GetProperty("availability").GetString().Should().Be(nameof(AvailabilityMode.Manual));

        PushCall push = _push.Calls.Should().ContainSingle().Subject;
        push.Message.Title.Should().Be("🎉 Квиз про именинника");
        push.Message.Body.Should().Be("Скорее участвуй!");
        push.Message.Tag.Should().Be($"event-{session.Id}");
        push.ExcludedUserId.Should().BeNull();
    }

    // === FinishEventAsync ===

    [Fact]
    public async Task FinishEventAsync_WithUnknownSession_Throws()
    {
        Func<Task> act = () => _service.FinishEventAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task FinishEventAsync_WithNotActiveSession_Throws()
    {
        await SeedAdminAsync();
        EventDefinition definition = await SeedDefinitionAsync();
        EventSession session = await SeedSessionAsync(definition, EventSessionState.Finished);

        Func<Task> act = () => _service.FinishEventAsync(session.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is not active*");
    }

    [Fact]
    public async Task FinishEventAsync_MarksFinishedNotifiesHandlerAndHub()
    {
        await SeedAdminAsync();
        EventDefinition definition = await SeedDefinitionAsync();
        EventSession session = await SeedSessionAsync(definition);
        _factory.GetHandler("quiz").Returns(_handler);

        await _service.FinishEventAsync(session.Id);

        EventSession? stored = await _host.DbAsync(db => db.EventSessions
            .AsNoTracking()
            .SingleAsync(s => s.Id == session.Id));
        stored.State.Should().Be(EventSessionState.Finished);
        stored.EndedAt.Should().NotBeNull();

        await _handler.Received(1).OnSessionFinishedAsync(
            Arg.Is<EventSession>(s => s.Id == session.Id),
            Arg.Any<EventDefinition>(),
            Arg.Any<CancellationToken>());

        RecordingHubContext.HubCall hubCall = _hub.SingleCall("EventFinished");
        Json(hubCall.Payload).GetProperty("sessionId").GetGuid().Should().Be(session.Id);
        _push.Calls.Should().BeEmpty();
    }

    // === SubmitToEventAsync ===

    [Fact]
    public async Task SubmitToEventAsync_WithUnknownSession_ReturnsFailure()
    {
        SubmissionOutcome outcome = await _service.SubmitToEventAsync(Guid.NewGuid(), Guid.NewGuid(), "{}");

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Be("Ивент не найден");
        await _handler.DidNotReceiveWithAnyArgs().HandleSubmissionAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task SubmitToEventAsync_WithNotActiveSession_ReturnsFailure()
    {
        await SeedAdminAsync();
        EventDefinition definition = await SeedDefinitionAsync();
        EventSession session = await SeedSessionAsync(definition, EventSessionState.Finished);

        SubmissionOutcome outcome = await _service.SubmitToEventAsync(session.Id, Guid.NewGuid(), "{}");

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Be("Ивент не активен");
        (await _host.DbAsync(db => db.PlayerSubmissions.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task SubmitToEventAsync_WithActiveSession_DelegatesToHandlerAndPersistsSubmission()
    {
        await SeedAdminAsync();
        User player = TestData.User("player");
        _host.Db.Users.Add(player);
        EventDefinition definition = await SeedDefinitionAsync();
        EventSession session = await SeedSessionAsync(definition);
        Guid playerId = player.Id;
        string payload = """{"questionIndex":0,"answerIndex":1}""";
        _factory.GetHandler("quiz").Returns(_handler);
        _handler.HandleSubmissionAsync(
                Arg.Any<EventSession>(),
                Arg.Any<EventDefinition>(),
                playerId,
                payload,
                Arg.Any<CancellationToken>())
            .Returns(SubmissionResult.Ok(10, "Правильно! 🎉", new { isCorrect = true }));

        SubmissionOutcome outcome = await _service.SubmitToEventAsync(session.Id, playerId, payload);

        outcome.Success.Should().BeTrue();
        outcome.PointsAwarded.Should().Be(10);
        outcome.Message.Should().Be("Правильно! 🎉");

        await _handler.Received(1).HandleSubmissionAsync(
            Arg.Is<EventSession>(s => s.Id == session.Id),
            Arg.Is<EventDefinition>(d => d.Id == definition.Id),
            playerId,
            payload,
            Arg.Any<CancellationToken>());

        PlayerSubmission submission = await _host.DbAsync(db => db.PlayerSubmissions.AsNoTracking().SingleAsync());
        submission.SessionId.Should().Be(session.Id);
        submission.PlayerId.Should().Be(playerId);
        submission.PayloadJson.Should().Be(payload);
        submission.Score.Should().Be(10);
        submission.SubmittedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SubmitToEventAsync_WhenHandlerFails_StillPersistsSubmissionWithZeroScore()
    {
        await SeedAdminAsync();
        User player = TestData.User("player");
        _host.Db.Users.Add(player);
        EventDefinition definition = await SeedDefinitionAsync();
        EventSession session = await SeedSessionAsync(definition);
        Guid playerId = player.Id;
        _factory.GetHandler("quiz").Returns(_handler);
        _handler.HandleSubmissionAsync(
                Arg.Any<EventSession>(),
                Arg.Any<EventDefinition>(),
                playerId,
                "{}",
                Arg.Any<CancellationToken>())
            .Returns(SubmissionResult.Fail("Неверный ответ"));

        SubmissionOutcome outcome = await _service.SubmitToEventAsync(session.Id, playerId, "{}");

        outcome.Success.Should().BeFalse();
        PlayerSubmission submission = await _host.DbAsync(db => db.PlayerSubmissions.AsNoTracking().SingleAsync());
        submission.Score.Should().Be(0);
    }

    // === GetEventDataAsync ===

    [Fact]
    public async Task GetEventDataAsync_WithUnknownSession_ReturnsNull()
    {
        (await _service.GetEventDataAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task GetEventDataAsync_ReturnsSessionInfoAndParsedConfig()
    {
        await SeedAdminAsync();
        string config = """{"pointsPerCorrect":10,"questions":[]}""";
        EventDefinition definition = await SeedDefinitionAsync(configJson: config, displayName: "Квиз");
        EventSession session = await SeedSessionAsync(definition);

        object? data = await _service.GetEventDataAsync(session.Id);

        data.Should().NotBeNull();
        JsonElement json = Json(data);
        json.GetProperty("sessionId").GetGuid().Should().Be(session.Id);
        json.GetProperty("type").GetString().Should().Be("quiz");
        json.GetProperty("displayName").GetString().Should().Be("Квиз");
        json.GetProperty("config").GetProperty("pointsPerCorrect").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task GetEventDataAsync_WithBrokenConfigJson_ReturnsEmptyConfig()
    {
        await SeedAdminAsync();
        EventDefinition definition = await SeedDefinitionAsync(configJson: "not-json");
        EventSession session = await SeedSessionAsync(definition);

        object? data = await _service.GetEventDataAsync(session.Id);

        data.Should().NotBeNull();
        Json(data).GetProperty("config").EnumerateObject().Should().BeEmpty();
    }
}