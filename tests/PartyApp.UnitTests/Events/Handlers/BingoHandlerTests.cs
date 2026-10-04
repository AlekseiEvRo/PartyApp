using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class BingoHandlerTests : IDisposable
{
    private const string SmallConfig = """
        {"size":3,"pointsPerCell":2,"lineBonus":10,"maxPredictions":3,"cells":["1","2","3","4","5","6","7","8","9"]}
        """;

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly SqliteTestHost _host = new();
    private readonly BingoService _bingo;
    private readonly BingoHandler _handler;

    public BingoHandlerTests()
    {
        _bingo = new BingoService(
            _host.ScopeFactory, _award, new RecordingHubContext(),
            TestAchievements.Create(_host.ScopeFactory), NullLogger<BingoService>.Instance);
        _handler = new BingoHandler(_host.ScopeFactory, _bingo, NullLogger<BingoHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

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
            Score = 0
        });
        await _host.Db.SaveChangesAsync();
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task FirstCell_IsMarkedWithoutPoints()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":0}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(0); // баллы — только после подтверждения админом
        result.Message.Should().Contain("Предсказание");

        Json(result.Data).GetProperty("markedCells").EnumerateArray()
            .Select(e => e.GetInt32()).Should().Equal(0);
        Json(result.Data).GetProperty("pendingCount").GetInt32().Should().Be(1);

        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
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

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public async Task InvalidCell_IsRejected(int cellIndex)
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, $$"""{"cellIndex":{{cellIndex}}}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("клетка");
    }

    [Fact]
    public async Task WithoutEnoughCells_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) =
            await SeedEventAsync("""{"size":5,"cells":["1","2"]}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":0}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("25");
    }

    [Fact]
    public async Task PredictionLimit_BlocksExtraUnconfirmedMarks()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();
        await SeedCellAsync(session.Id, playerId, 0);
        await SeedCellAsync(session.Id, playerId, 1);
        await SeedCellAsync(session.Id, playerId, 2);

        SubmissionResult blocked = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":3}""");

        blocked.Success.Should().BeFalse();
        blocked.Message.Should().Contain("Лимит предсказаний");
    }

    [Fact]
    public async Task RejectedCell_FreesPredictionSlot()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();
        await SeedCellAsync(session.Id, playerId, 0);
        await SeedCellAsync(session.Id, playerId, 1);
        await SeedCellAsync(session.Id, playerId, 2);

        await _bingo.RejectCellAsync(session.Id, 0);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":3}""");

        result.Success.Should().BeTrue();
        Json(result.Data).GetProperty("pendingCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task ConfirmedCell_DoesNotConsumeLimitAndGivesNoPoints()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();
        await SeedCellAsync(session.Id, playerId, 0);
        await SeedCellAsync(session.Id, playerId, 1);
        await SeedCellAsync(session.Id, playerId, 2);

        // Пустую клетку 8 админ подтвердил заранее
        await _bingo.ConfirmCellAsync(session.Id, 8);

        // Поздняя отметка подтверждённой клетки проходит и не расходует слот
        SubmissionResult late = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":8}""");

        late.Success.Should().BeTrue();
        late.Message.Should().Contain("не начисляются");
        Json(late.Data).GetProperty("pendingCount").GetInt32().Should().Be(3);

        // Свободных слотов по-прежнему нет: четвёртое предсказание не проходит
        SubmissionResult blocked = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":3}""");

        blocked.Success.Should().BeFalse();
        blocked.Message.Should().Contain("Лимит предсказаний");
    }

    [Fact]
    public async Task RejectedCell_CannotBeMarked()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();
        await _bingo.RejectCellAsync(session.Id, 0);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"cellIndex":0}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("не было");
    }

    [Fact]
    public async Task PlayerData_SeparatesMarksFromConfirmedCells()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();
        await SeedCellAsync(session.Id, playerId, 0);
        await SeedCellAsync(session.Id, playerId, 1);

        BingoConfirmOutcome confirmed = await _bingo.ConfirmCellAsync(session.Id, 0);
        confirmed.Success.Should().BeTrue();

        JsonElement data = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));

        data.GetProperty("markedCells").EnumerateArray().Select(e => e.GetInt32())
            .Should().BeEquivalentTo(new[] { 0, 1 });
        data.GetProperty("confirmedCells").EnumerateArray().Select(e => e.GetInt32())
            .Should().Equal(0);
        data.GetProperty("pendingCells").EnumerateArray().Select(e => e.GetInt32())
            .Should().Equal(1);
    }
}