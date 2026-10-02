using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class DareHandlerTests : IDisposable
{
    private const string ThreeTasksConfig = """{"points":7,"tasks":["Раз","Два","Три"]}""";

    private readonly SqliteTestHost _host = new();
    private readonly DareHandler _handler;

    public DareHandlerTests()
    {
        _handler = new DareHandler(_host.ScopeFactory, NullLogger<DareHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync(
        string configJson = ThreeTasksConfig,
        string username = "player")
    {
        User player = TestData.User(username);
        EventDefinition definition = TestData.Definition("dare", configJson);
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    private Task<DareAssignment> GetAssignmentAsync(Guid sessionId, Guid playerId)
    {
        return _host.DbAsync(db => db.DareAssignments.AsNoTracking()
            .SingleAsync(a => a.SessionId == sessionId && a.PlayerId == playerId));
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task FirstDare_CreatesPendingAssignmentWithoutPoints()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(0); // баллы только после подтверждения
        result.Message.Should().Contain("Жди подтверждения");

        JsonElement data = Json(result.Data);
        data.GetProperty("status").GetString().Should().Be("pending");
        data.GetProperty("task").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("points").GetInt32().Should().Be(7);

        DareAssignment assignment = await GetAssignmentAsync(session.Id, playerId);
        assignment.Status.Should().Be(DareStatus.Pending);
        assignment.Points.Should().Be(7);
    }

    [Fact]
    public async Task SecondDare_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
        SubmissionResult second = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже");
        Json(second.Data).GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task DifferentPlayers_GetDifferentTasksWhenPossible()
    {
        (EventDefinition definition, EventSession session, Guid first) =
            await SeedEventAsync("""{"points":5,"tasks":["Раз","Два"]}""", "first");
        Guid second = await SeedSecondPlayerAsync(session);

        await _handler.HandleSubmissionAsync(session, definition, first, "{}");
        await _handler.HandleSubmissionAsync(session, definition, second, "{}");

        DareAssignment firstAssignment = await GetAssignmentAsync(session.Id, first);
        DareAssignment secondAssignment = await GetAssignmentAsync(session.Id, second);

        firstAssignment.TaskIndex.Should().NotBe(secondAssignment.TaskIndex);
    }

    [Fact]
    public async Task MorePlayersThanTasks_StillAssigns()
    {
        (EventDefinition definition, EventSession session, Guid first) =
            await SeedEventAsync("""{"points":5,"tasks":["Единственный"]}""", "first");
        Guid second = await SeedSecondPlayerAsync(session);

        SubmissionResult firstResult = await _handler.HandleSubmissionAsync(session, definition, first, "{}");
        SubmissionResult secondResult = await _handler.HandleSubmissionAsync(session, definition, second, "{}");

        firstResult.Success.Should().BeTrue();
        secondResult.Success.Should().BeTrue();
        (await GetAssignmentAsync(session.Id, second)).TaskIndex.Should().Be(0);
    }

    [Fact]
    public async Task WithoutTasks_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) =
            await SeedEventAsync("""{"points":5,"tasks":[]}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("не настроены");
    }

    [Fact]
    public async Task GetPlayerData_ReflectsDareStatus()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        JsonElement before = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));
        before.GetProperty("dare").ValueKind.Should().Be(JsonValueKind.Null);

        await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        JsonElement pending = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));
        pending.GetProperty("dare").GetProperty("status").GetString().Should().Be("pending");

        await _host.DbAsync(async db =>
        {
            DareAssignment assignment = await db.DareAssignments
                .SingleAsync(a => a.SessionId == session.Id && a.PlayerId == playerId);
            assignment.Status = DareStatus.Confirmed;
            await db.SaveChangesAsync();
        });

        JsonElement confirmed = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));
        confirmed.GetProperty("dare").GetProperty("status").GetString().Should().Be("confirmed");
    }

    private async Task<Guid> SeedSecondPlayerAsync(EventSession session)
    {
        User player = TestData.User("second");
        _host.Db.Users.Add(player);
        await _host.Db.SaveChangesAsync();
        return player.Id;
    }
}