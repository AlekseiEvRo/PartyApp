using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class BingoHandlerTests : IDisposable
{
    private const string SmallConfig = """
        {"size":3,"pointsPerCell":2,"lineBonus":10,"cells":["1","2","3","4","5","6","7","8","9"]}
        """;

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly SqliteTestHost _host = new();
    private readonly BingoHandler _handler;

    public BingoHandlerTests()
    {
        _handler = new BingoHandler(_host.ScopeFactory, _award, NullLogger<BingoHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private static (EventDefinition Definition, EventSession Session) CreateEvent(string configJson = SmallConfig)
    {
        EventDefinition definition = TestData.Definition("bingo", configJson);
        EventSession session = TestData.Session(definition, Guid.NewGuid());
        return (definition, session);
    }

    /// <summary>Сохраняет игрока, определение и сессию — нужны для FK при подсеве сабмитов.</summary>
    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync(
        string configJson = SmallConfig)
    {
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("bingo", configJson);
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    private async Task SeedCellAsync(Guid sessionId, Guid playerId, int cellIndex)
    {
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = sessionId,
            PlayerId = playerId,
            PayloadJson = $$"""{"cellIndex":{{cellIndex}}}""",
            Score = 2
        });
        await _host.Db.SaveChangesAsync();
    }

    private static System.Text.Json.JsonElement Json(object? value)
    {
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task FirstCell_IsMarkedAndAwarded()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        Guid playerId = Guid.NewGuid();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":0}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(2);
        Json(result.Data).GetProperty("markedCells").EnumerateArray()
            .Select(e => e.GetInt32()).Should().Equal(0);
    }

    [Fact]
    public async Task DuplicateCell_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        await SeedCellAsync(session.Id, playerId, 4);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":4}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Уже отмечено");
    }

    [Fact]
    public async Task CompletedLine_AddsBonus()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        await SeedCellAsync(session.Id, playerId, 0);
        await SeedCellAsync(session.Id, playerId, 1);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":2}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(2 + 10); // клетка + линия
        Json(result.Data).GetProperty("lines").GetInt32().Should().Be(1);
        Json(result.Data).GetProperty("newLines").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task TwoLinesAtOnce_AddDoubleBonus()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        // Центр (4) завершает и строку (3,4,5), и столбец (1,4,7)
        await SeedCellAsync(session.Id, playerId, 3);
        await SeedCellAsync(session.Id, playerId, 5);
        await SeedCellAsync(session.Id, playerId, 1);
        await SeedCellAsync(session.Id, playerId, 7);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":4}""");

        result.PointsAwarded.Should().Be(2 + 20);
        Json(result.Data).GetProperty("newLines").GetInt32().Should().Be(2);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public async Task InvalidCell_IsRejected(int cellIndex)
    {
        (EventDefinition definition, EventSession session) = CreateEvent();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), $$"""{"cellIndex":{{cellIndex}}}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("клетка");
    }

    [Fact]
    public async Task WithoutEnoughCells_IsRejected()
    {
        (EventDefinition definition, EventSession session) = CreateEvent("""{"size":5,"cells":["1","2"]}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"cellIndex":0}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("25");
    }

    [Fact]
    public async Task GetLiveData_CountsMarksAndPlayers()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();
        await SeedCellAsync(session.Id, playerId, 0);
        await SeedCellAsync(session.Id, playerId, 1);

        JsonElement live = Json(await _handler.GetLiveDataAsync(session, definition));

        live.GetProperty("markedCount").GetInt32().Should().Be(2);
        live.GetProperty("playersCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetPlayerData_ReturnsMarksAndLines()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        await SeedCellAsync(session.Id, playerId, 0);
        await SeedCellAsync(session.Id, playerId, 1);

        AssertPlayerData(await _handler.GetPlayerDataAsync(session, definition, playerId));
    }

    private static void AssertPlayerData(object? data)
    {
        System.Text.Json.JsonElement json = Json(data);
        json.GetProperty("markedCells").GetArrayLength().Should().Be(2);
        json.GetProperty("lines").GetInt32().Should().Be(0);
    }
}