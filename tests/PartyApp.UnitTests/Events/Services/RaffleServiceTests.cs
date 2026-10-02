using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Services;

public class RaffleServiceTests : IDisposable
{
    private readonly RecordingHubContext _hub = new();
    private readonly SqliteTestHost _host = new();
    private readonly RaffleService _service;

    public RaffleServiceTests()
    {
        _service = new RaffleService(_host.ScopeFactory, _hub, NullLogger<RaffleService>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private async Task<(EventSession Session, Guid PlayerId)> SeedEventAsync(string type = "raffle")
    {
        User player = TestData.User($"u-{Guid.NewGuid():N}"[..10]);
        EventDefinition definition = TestData.Definition(type, "{}");
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (session, player.Id);
    }

    private async Task<Guid> SeedParticipantAsync(EventSession session)
    {
        User player = TestData.User($"u-{Guid.NewGuid():N}"[..10]);
        _host.Db.Users.Add(player);
        await _host.Db.SaveChangesAsync();

        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = player.Id,
            PayloadJson = "{}",
            Score = 0
        });
        await _host.Db.SaveChangesAsync();

        return player.Id;
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task Draw_PicksParticipantAndBroadcasts()
    {
        (EventSession session, _) = await SeedEventAsync();
        Guid first = await SeedParticipantAsync(session);
        Guid second = await SeedParticipantAsync(session);

        RaffleDrawOutcome outcome = await _service.DrawAsync(session.Id);

        outcome.Success.Should().BeTrue();

        RaffleDraw draw = await _host.DbAsync(db => db.RaffleDraws.AsNoTracking()
            .SingleAsync(d => d.SessionId == session.Id));
        new[] { first, second }.Should().Contain(draw.WinnerId);

        JsonElement payload = Json(_hub.SingleCall("RaffleDrawn").Payload);
        payload.GetProperty("participants").GetArrayLength().Should().Be(2);
        payload.GetProperty("winner").GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Draw_Twice_IsRejected()
    {
        (EventSession session, _) = await SeedEventAsync();
        await SeedParticipantAsync(session);

        await _service.DrawAsync(session.Id);
        RaffleDrawOutcome second = await _service.DrawAsync(session.Id);

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже проводился");
    }

    [Fact]
    public async Task Draw_WithoutParticipants_IsRejected()
    {
        (EventSession session, _) = await SeedEventAsync();

        RaffleDrawOutcome outcome = await _service.DrawAsync(session.Id);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("никто");
    }

    [Fact]
    public async Task Draw_NonRaffleSession_IsRejected()
    {
        (EventSession session, _) = await SeedEventAsync(type: "quiz");

        RaffleDrawOutcome outcome = await _service.DrawAsync(session.Id);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("не найдена");
    }

    [Fact]
    public async Task State_ReturnsParticipantsAndWinner()
    {
        (EventSession session, _) = await SeedEventAsync();
        Guid participant = await SeedParticipantAsync(session);

        JsonElement before = Json(await _service.GetStateAsync(session.Id));
        before.GetProperty("participants").GetArrayLength().Should().Be(1);
        before.GetProperty("winner").ValueKind.Should().Be(JsonValueKind.Null);

        await _service.DrawAsync(session.Id);

        JsonElement after = Json(await _service.GetStateAsync(session.Id));
        after.GetProperty("winner").GetProperty("id").GetGuid().Should().Be(participant);
    }
}

public class RaffleHandlerTests : IDisposable
{
    private readonly RecordingHubContext _hub = new();
    private readonly SqliteTestHost _host = new();
    private readonly RaffleHandler _handler;

    public RaffleHandlerTests()
    {
        RaffleService service = new(_host.ScopeFactory, _hub, NullLogger<RaffleService>.Instance);
        _handler = new RaffleHandler(_host.ScopeFactory, service, NullLogger<RaffleHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync()
    {
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("raffle", """{"prize":"Приз"}""");
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    /// <summary>Симулирует сохранение заявки: в проде это делает EventService.</summary>
    private async Task PersistJoinAsync(EventSession session, Guid playerId)
    {
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = playerId,
            PayloadJson = "{}",
            Score = 0
        });
        await _host.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Join_RegistersParticipantWithoutPoints()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(0);
        Json(result.Data).GetProperty("joined").GetBoolean().Should().BeTrue();
        Json(result.Data).GetProperty("participants").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Join_Twice_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
        await PersistJoinAsync(session, playerId);

        SubmissionResult second = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже участвуешь");
    }

    [Fact]
    public async Task PlayerData_ReflectsParticipation()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        JsonElement before = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));
        before.GetProperty("joined").GetBoolean().Should().BeFalse();

        await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
        await PersistJoinAsync(session, playerId);

        JsonElement after = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));
        after.GetProperty("joined").GetBoolean().Should().BeTrue();
        after.GetProperty("participants").GetInt32().Should().Be(1);
    }
}