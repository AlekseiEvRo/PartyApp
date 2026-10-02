using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Services;

public class BingoServiceTests : IDisposable
{
    private const string SmallConfig = """
        {"size":3,"pointsPerCell":2,"lineBonus":10,"cells":["1","2","3","4","5","6","7","8","9"]}
        """;

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly RecordingHubContext _hub = new();
    private readonly SqliteTestHost _host = new();
    private readonly BingoService _service;

    public BingoServiceTests()
    {
        _service = new BingoService(
            _host.ScopeFactory, _award, _hub, NullLogger<BingoService>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync(
        string configJson = SmallConfig)
    {
        User player = TestData.User($"u-{Guid.NewGuid():N}"[..10]);
        EventDefinition definition = TestData.Definition("bingo", configJson);
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    private async Task<Guid> SeedSecondPlayerAsync()
    {
        User player = TestData.User($"u-{Guid.NewGuid():N}"[..10]);
        _host.Db.Users.Add(player);
        await _host.Db.SaveChangesAsync();
        return player.Id;
    }

    private async Task SeedMarkAsync(Guid sessionId, Guid playerId, int cellIndex)
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
    public async Task ConfirmCell_AwardsOnlyPlayersWhoMarkedIt()
    {
        (_, EventSession session, Guid marker) = await SeedEventAsync();
        Guid other = await SeedSecondPlayerAsync();

        await SeedMarkAsync(session.Id, marker, 0);
        await SeedMarkAsync(session.Id, other, 5);

        BingoConfirmOutcome outcome = await _service.ConfirmCellAsync(session.Id, 0);

        outcome.Success.Should().BeTrue();
        await _award.Received(1).AwardAsync(
            marker, 2, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.DidNotReceive().AwardAsync(
            other, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        Json(outcome.Data).GetProperty("awardedPlayers").GetInt32().Should().Be(1);
        _hub.SingleCall("BingoCellConfirmed").Should().NotBeNull();
    }

    [Fact]
    public async Task ConfirmCell_LineBonusCountsOnlyConfirmedCells()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0);
        await SeedMarkAsync(session.Id, player, 1);
        await SeedMarkAsync(session.Id, player, 2);

        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.ConfirmCellAsync(session.Id, 1);
        await _service.ConfirmCellAsync(session.Id, 2); // закрывает линию

        await _award.Received(2).AwardAsync(
            player, 2, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.Received(1).AwardAsync(
            player, 2 + 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmCell_Twice_IsRejected()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0);

        await _service.ConfirmCellAsync(session.Id, 0);
        BingoConfirmOutcome second = await _service.ConfirmCellAsync(session.Id, 0);

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже подтверждена");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public async Task ConfirmCell_InvalidCell_IsRejected(int cellIndex)
    {
        (_, EventSession session, _) = await SeedEventAsync();

        BingoConfirmOutcome outcome = await _service.ConfirmCellAsync(session.Id, cellIndex);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("клетка");
    }

    [Fact]
    public async Task ConfirmCell_NonBingoSession_IsRejected()
    {
        User player = TestData.User("quizzer");
        EventDefinition definition = TestData.Definition("quiz", "{}");
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        BingoConfirmOutcome outcome = await _service.ConfirmCellAsync(session.Id, 0);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("не найдена");
    }

    [Fact]
    public async Task AdminState_ShowsMarksAndConfirmations()
    {
        (_, EventSession session, Guid first) = await SeedEventAsync();
        Guid second = await SeedSecondPlayerAsync();

        await SeedMarkAsync(session.Id, first, 0);
        await SeedMarkAsync(session.Id, second, 0);
        await SeedMarkAsync(session.Id, first, 4);
        await _service.ConfirmCellAsync(session.Id, 4);

        JsonElement state = Json(await _service.GetAdminStateAsync(session.Id));

        state.GetProperty("playersCount").GetInt32().Should().Be(2);
        state.GetProperty("confirmedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(4);
        state.GetProperty("markCounts").GetProperty("0").GetInt32().Should().Be(2);
        state.GetProperty("markCounts").GetProperty("4").GetInt32().Should().Be(1);
    }
}