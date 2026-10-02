using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class DareHandlerTests : IDisposable
{
    private const string ThreeTasksConfig = """{"points":5,"tasks":["Раз","Два","Три"]}""";

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly SqliteTestHost _host = new();
    private readonly DareHandler _handler;

    public DareHandlerTests()
    {
        _handler = new DareHandler(_host.ScopeFactory, _award, NullLogger<DareHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private static (EventDefinition Definition, EventSession Session) CreateEvent(
        string configJson = ThreeTasksConfig)
    {
        EventDefinition definition = TestData.Definition("dare", configJson);
        EventSession session = TestData.Session(definition, Guid.NewGuid());
        return (definition, session);
    }

    /// <summary>Сохраняет игрока, определение и сессию — нужны для FK при подсеве сабмитов.</summary>
    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync(
        string configJson = ThreeTasksConfig)
    {
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("dare", configJson);
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    private async Task SeedTaskAsync(Guid sessionId, Guid playerId, int taskIndex)
    {
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = sessionId,
            PlayerId = playerId,
            PayloadJson = $$"""{"taskIndex":{{taskIndex}}}""",
            Score = 5
        });
        await _host.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task FirstDare_AssignsTaskAndAwardsPoints()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        Guid playerId = Guid.NewGuid();

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(5);

        await _award.Received(1).AwardAsync(
            playerId, 5, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UsedTasks_AreNotAssignedAgain()
    {
        (EventDefinition definition, EventSession session, Guid playerId) =
            await SeedEventAsync("""{"points":5,"tasks":["Раз","Два"]}""");

        await SeedTaskAsync(session.Id, playerId, taskIndex: 0);

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        result.Success.Should().BeTrue();
        System.Text.Json.JsonElement data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(result.Data));
        data.GetProperty("taskIndex").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task AllTasksUsed_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) =
            await SeedEventAsync("""{"points":5,"tasks":["Раз","Два"]}""");

        await SeedTaskAsync(session.Id, playerId, 0);
        await SeedTaskAsync(session.Id, playerId, 1);

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("все фанты");
    }

    [Fact]
    public async Task WithoutTasks_IsRejected()
    {
        (EventDefinition definition, EventSession session) = CreateEvent("""{"points":5,"tasks":[]}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, Guid.NewGuid(), "{}");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("не настроены");
    }

    [Fact]
    public async Task GetPlayerData_CountsCompletedDares()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        await SeedTaskAsync(session.Id, playerId, 0);
        await SeedTaskAsync(session.Id, playerId, 2);

        object? data = await _handler.GetPlayerDataAsync(session, definition, playerId);
        System.Text.Json.JsonElement json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(data));

        json.GetProperty("completed").GetInt32().Should().Be(2);
        json.GetProperty("total").GetInt32().Should().Be(3);
    }
}