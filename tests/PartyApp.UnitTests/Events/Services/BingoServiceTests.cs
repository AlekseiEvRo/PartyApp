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

    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly RecordingHubContext _hub = new();
    private readonly SqliteTestHost _host = new();
    private readonly BingoService _service;

    public BingoServiceTests()
    {
        _service = new BingoService(
            _host.ScopeFactory, _award, _hub, TestAchievements.Create(_host.ScopeFactory), NullLogger<BingoService>.Instance);
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

    private async Task SeedMarkAsync(Guid sessionId, Guid playerId, int cellIndex, DateTime? submittedAt = null)
    {
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = sessionId,
            PlayerId = playerId,
            PayloadJson = $$"""{"cellIndex":{{cellIndex}}}""",
            Score = 0,
            SubmittedAt = submittedAt ?? DateTime.UtcNow
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

        await SeedMarkAsync(session.Id, marker, 0, Start);
        await SeedMarkAsync(session.Id, other, 5, Start);

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
    public async Task ConfirmCell_LineBonusGoesOnlyToFastest()
    {
        (_, EventSession session, Guid fast) = await SeedEventAsync();
        Guid slow = await SeedSecondPlayerAsync();

        await SeedMarkAsync(session.Id, fast, 0, Start);
        await SeedMarkAsync(session.Id, fast, 1, Start.AddMinutes(1));
        await SeedMarkAsync(session.Id, fast, 2, Start.AddMinutes(2));

        await SeedMarkAsync(session.Id, slow, 0, Start.AddMinutes(3));
        await SeedMarkAsync(session.Id, slow, 1, Start.AddMinutes(4));
        await SeedMarkAsync(session.Id, slow, 2, Start.AddMinutes(5));

        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.ConfirmCellAsync(session.Id, 1);
        await _service.ConfirmCellAsync(session.Id, 2);

        // Оба получили по 2 балла за каждое сбывшееся предсказание
        await _award.Received(3).AwardAsync(
            fast, 2, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.Received(3).AwardAsync(
            slow, 2, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        // Бонус за линию — только самому быстрому
        await _award.Received(1).AwardAsync(
            fast, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.DidNotReceive().AwardAsync(
            slow, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmCell_LineBonusRequiresAllCellsConfirmed()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await SeedMarkAsync(session.Id, player, 1, Start.AddMinutes(1));
        await SeedMarkAsync(session.Id, player, 2, Start.AddMinutes(2));

        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.ConfirmCellAsync(session.Id, 1);

        await _award.DidNotReceive().AwardAsync(
            player, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        await _service.ConfirmCellAsync(session.Id, 2);

        await _award.Received(1).AwardAsync(
            player, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LineAward_IsPaidOnlyOnce()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await SeedMarkAsync(session.Id, player, 1, Start.AddMinutes(1));
        await SeedMarkAsync(session.Id, player, 2, Start.AddMinutes(2));

        await _service.ConfirmCellAsync(session.Id, 2);
        await _service.ConfirmCellAsync(session.Id, 1);
        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.CheckLineAwardsAsync(session.Id);

        await _award.Received(1).AwardAsync(
            player, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckLineAwards_ConfirmedLineCompletedLater_PaysFirstCompleter()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();

        // Все клетки линии подтвердили до того, как кто-то её собрал
        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.ConfirmCellAsync(session.Id, 1);
        await _service.ConfirmCellAsync(session.Id, 2);
        _award.ClearReceivedCalls();

        DateTime t = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await SeedMarkAsync(session.Id, player, 0, t);
        await SeedMarkAsync(session.Id, player, 1, t.AddSeconds(1));
        await SeedMarkAsync(session.Id, player, 2, t.AddSeconds(2));

        await _service.CheckLineAwardsAsync(session.Id);

        await _award.Received(1).AwardAsync(
            player, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayerState_ClassifiesPendingPredictionAndLateMarks()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();

        await SeedMarkAsync(session.Id, player, 0, Start); // останется ждать решения
        await SeedMarkAsync(session.Id, player, 1, Start); // сбывшееся предсказание

        await _service.ConfirmCellAsync(session.Id, 1);
        await _service.ConfirmCellAsync(session.Id, 2); // пустая клетка подтверждена
        await SeedMarkAsync(session.Id, player, 2, DateTime.UtcNow.AddSeconds(1)); // поздняя отметка

        JsonElement state = Json(await _service.GetPlayerStateAsync(session.Id, player));

        state.GetProperty("pendingCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(0);
        state.GetProperty("predictionCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(1);
        state.GetProperty("lateCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(2);
        state.GetProperty("pendingCount").GetInt32().Should().Be(1);
        state.GetProperty("maxPredictions").GetInt32().Should().Be(3);
        state.GetProperty("markedCells").EnumerateArray().Select(e => e.GetInt32())
            .Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [Fact]
    public async Task LiveState_IncludesGridAndAdminDecisions()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);

        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.RejectCellAsync(session.Id, 1);

        JsonElement live = Json(await _service.GetLiveStateAsync(session.Id));

        live.GetProperty("size").GetInt32().Should().Be(3);
        live.GetProperty("cells").EnumerateArray().Select(e => e.GetString())
            .Should().HaveCount(9);
        live.GetProperty("confirmedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(0);
        live.GetProperty("rejectedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(1);
        live.GetProperty("confirmedCount").GetInt32().Should().Be(1);
        live.GetProperty("rejectedCount").GetInt32().Should().Be(1);
        live.GetProperty("playersCount").GetInt32().Should().Be(1);

        // После каждого решения админа экран получает свежие live-данные
        _hub.CallsFor("EventLiveUpdated").Should().HaveCount(2);
    }

    [Fact]
    public async Task RejectCell_BlocksConfirmAndFreesPredictionSlot()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);

        BingoConfirmOutcome reject = await _service.RejectCellAsync(session.Id, 0);

        reject.Success.Should().BeTrue();
        _hub.SingleCall("BingoCellRejected").Should().NotBeNull();

        // Отклонённую клетку нельзя подтвердить
        BingoConfirmOutcome confirm = await _service.ConfirmCellAsync(session.Id, 0);
        confirm.Success.Should().BeFalse();
        confirm.Message.Should().Contain("отклонена");

        // Повторно отклонить тоже нельзя
        BingoConfirmOutcome secondReject = await _service.RejectCellAsync(session.Id, 0);
        secondReject.Success.Should().BeFalse();
        secondReject.Message.Should().Contain("уже отклонена");

        // Отметка на отклонённой клетке больше не занимает слот предсказания
        JsonElement state = Json(await _service.GetPlayerStateAsync(session.Id, player));
        state.GetProperty("pendingCount").GetInt32().Should().Be(0);
        state.GetProperty("rejectedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(0);
    }

    [Fact]
    public async Task ConfirmCell_ThenReject_IsRejected()
    {
        (_, EventSession session, _) = await SeedEventAsync();

        await _service.ConfirmCellAsync(session.Id, 0);
        BingoConfirmOutcome reject = await _service.RejectCellAsync(session.Id, 0);

        reject.Success.Should().BeFalse();
        reject.Message.Should().Contain("уже подтверждена");
    }

    [Fact]
    public async Task ConfirmCell_Twice_IsRejected()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);

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
    public async Task AdminState_WithExtraCells_ShowsOnlyPlayableGrid()
    {
        const string configWithExtraCells = """
            {"size":3,"pointsPerCell":2,"lineBonus":10,"cells":["1","2","3","4","5","6","7","8","9","лишняя","ещё лишняя"]}
            """;
        (_, EventSession session, _) = await SeedEventAsync(configWithExtraCells);

        JsonElement state = Json(await _service.GetAdminStateAsync(session.Id));

        // Админка показывает ту же сетку, что и игроки: только size * size клеток
        state.GetProperty("size").GetInt32().Should().Be(3);
        state.GetProperty("cells").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("1", "2", "3", "4", "5", "6", "7", "8", "9");
    }

    [Fact]
    public async Task AdminState_ShowsMarksAndConfirmations()
    {
        (_, EventSession session, Guid first) = await SeedEventAsync();
        Guid second = await SeedSecondPlayerAsync();

        await SeedMarkAsync(session.Id, first, 0, Start);
        await SeedMarkAsync(session.Id, second, 0, Start);
        await SeedMarkAsync(session.Id, first, 4, Start);
        await _service.ConfirmCellAsync(session.Id, 4);

        JsonElement state = Json(await _service.GetAdminStateAsync(session.Id));

        state.GetProperty("playersCount").GetInt32().Should().Be(2);
        state.GetProperty("confirmedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(4);
        state.GetProperty("markCounts").GetProperty("0").GetInt32().Should().Be(2);
        state.GetProperty("markCounts").GetProperty("4").GetInt32().Should().Be(1);
        state.GetProperty("maxPredictions").GetInt32().Should().Be(3);
    }
}