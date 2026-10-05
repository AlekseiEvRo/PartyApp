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

    private async Task LockAsync(Guid sessionId)
    {
        BingoConfirmOutcome outcome = await _service.LockAsync(sessionId);
        outcome.Success.Should().BeTrue();
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task ConfirmCell_WithoutLock_IsRejected()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);

        BingoConfirmOutcome outcome = await _service.ConfirmCellAsync(session.Id, 0);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("Сначала завершите приём");
    }

    [Fact]
    public async Task RejectCell_WithoutLock_IsRejected()
    {
        (_, EventSession session, _) = await SeedEventAsync();

        BingoConfirmOutcome outcome = await _service.RejectCellAsync(session.Id, 0);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("Сначала завершите приём");
    }

    [Fact]
    public async Task Lock_Twice_IsRejected()
    {
        (_, EventSession session, _) = await SeedEventAsync();
        await LockAsync(session.Id);

        BingoConfirmOutcome second = await _service.LockAsync(session.Id);

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже закрыт");
        _hub.SingleCall("BingoLocked").Should().NotBeNull();
    }

    [Fact]
    public async Task Unlock_WithoutDecisions_ReopensAnswers()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await LockAsync(session.Id);

        BingoConfirmOutcome unlock = await _service.UnlockAsync(session.Id);

        unlock.Success.Should().BeTrue();
        Json(unlock.Data).GetProperty("locked").GetBoolean().Should().BeFalse();

        // После возврата приёма выбор снова можно менять
        BingoConfirmOutcome removed = await _service.RemoveMarkAsync(session.Id, player, 0);
        removed.Success.Should().BeTrue();

        JsonElement state = Json(await _service.GetPlayerStateAsync(session.Id, player));
        state.GetProperty("locked").GetBoolean().Should().BeFalse();
        state.GetProperty("selectedCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Unlock_AfterDecision_IsRejected()
    {
        (_, EventSession session, _) = await SeedEventAsync();
        await LockAsync(session.Id);
        await _service.ConfirmCellAsync(session.Id, 0);

        BingoConfirmOutcome unlock = await _service.UnlockAsync(session.Id);

        unlock.Success.Should().BeFalse();
        unlock.Message.Should().Contain("решения по клеткам");
    }

    [Fact]
    public async Task ConfirmCell_AwardsOnlyPlayersWhoMarkedIt()
    {
        (_, EventSession session, Guid marker) = await SeedEventAsync();
        Guid other = await SeedSecondPlayerAsync();

        await SeedMarkAsync(session.Id, marker, 0, Start);
        await SeedMarkAsync(session.Id, other, 5, Start);
        await LockAsync(session.Id);

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
    public async Task ConfirmCell_LineBonusGoesToEveryoneWhoCompleted()
    {
        (_, EventSession session, Guid fast) = await SeedEventAsync();
        Guid slow = await SeedSecondPlayerAsync();

        await SeedMarkAsync(session.Id, fast, 0, Start);
        await SeedMarkAsync(session.Id, fast, 1, Start.AddMinutes(1));
        await SeedMarkAsync(session.Id, fast, 2, Start.AddMinutes(2));

        await SeedMarkAsync(session.Id, slow, 0, Start.AddMinutes(3));
        await SeedMarkAsync(session.Id, slow, 1, Start.AddMinutes(4));
        await SeedMarkAsync(session.Id, slow, 2, Start.AddMinutes(5));

        await LockAsync(session.Id);
        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.ConfirmCellAsync(session.Id, 1);
        await _service.ConfirmCellAsync(session.Id, 2);

        // Оба получили по 2 балла за каждое сбывшееся предсказание
        await _award.Received(3).AwardAsync(
            fast, 2, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.Received(3).AwardAsync(
            slow, 2, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        // Бонус за линию получают оба, каждый по разу
        await _award.Received(1).AwardAsync(
            fast, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.Received(1).AwardAsync(
            slow, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmCell_PaysBonusForEachCompletedLine()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();

        // Два ряда: клетки 0..2 и 3..5
        for (int cell = 0; cell <= 5; cell++)
            await SeedMarkAsync(session.Id, player, cell, Start.AddMinutes(cell));

        await LockAsync(session.Id);
        for (int cell = 0; cell <= 5; cell++)
            await _service.ConfirmCellAsync(session.Id, cell);

        // По бонусу за каждый собранный ряд
        await _award.Received(2).AwardAsync(
            player, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmCell_LineBonusRequiresAllCellsConfirmed()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await SeedMarkAsync(session.Id, player, 1, Start.AddMinutes(1));
        await SeedMarkAsync(session.Id, player, 2, Start.AddMinutes(2));
        await LockAsync(session.Id);

        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.ConfirmCellAsync(session.Id, 1);

        await _award.DidNotReceive().AwardAsync(
            player, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        await _service.ConfirmCellAsync(session.Id, 2);

        await _award.Received(1).AwardAsync(
            player, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LineAward_IsPaidOnlyOncePerPlayer()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await SeedMarkAsync(session.Id, player, 1, Start.AddMinutes(1));
        await SeedMarkAsync(session.Id, player, 2, Start.AddMinutes(2));
        await LockAsync(session.Id);

        await _service.ConfirmCellAsync(session.Id, 2);
        await _service.ConfirmCellAsync(session.Id, 1);
        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.CheckLineAwardsAsync(session.Id);

        await _award.Received(1).AwardAsync(
            player, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckLineAwards_ConfirmedLineCompletedLater_PaysCompleter()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await LockAsync(session.Id);

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
    public async Task PlayerState_ClassifiesSelectionAndDecisions()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();

        await SeedMarkAsync(session.Id, player, 0, Start); // останется ждать решения
        await SeedMarkAsync(session.Id, player, 1, Start); // сбывшееся предсказание
        await LockAsync(session.Id);

        await _service.ConfirmCellAsync(session.Id, 1);
        await _service.ConfirmCellAsync(session.Id, 2); // пустая клетка подтверждена

        JsonElement state = Json(await _service.GetPlayerStateAsync(session.Id, player));

        state.GetProperty("locked").GetBoolean().Should().BeTrue();
        state.GetProperty("pendingCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(0);
        state.GetProperty("predictionCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(1);
        state.GetProperty("allConfirmedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(1, 2);
        state.GetProperty("selectedCount").GetInt32().Should().Be(2);
        state.GetProperty("pendingCount").GetInt32().Should().Be(1);
        state.GetProperty("maxPredictions").GetInt32().Should().Be(5); // половина поля 3×3, округление вверх
        state.GetProperty("markedCells").EnumerateArray().Select(e => e.GetInt32())
            .Should().BeEquivalentTo(new[] { 0, 1 });
    }

    [Fact]
    public async Task LiveState_IncludesGridAndAdminDecisions()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await LockAsync(session.Id);

        await _service.ConfirmCellAsync(session.Id, 0);
        await _service.RejectCellAsync(session.Id, 1);

        JsonElement live = Json(await _service.GetLiveStateAsync(session.Id));

        live.GetProperty("size").GetInt32().Should().Be(3);
        live.GetProperty("cells").EnumerateArray().Select(e => e.GetString())
            .Should().HaveCount(9);
        live.GetProperty("locked").GetBoolean().Should().BeTrue();
        live.GetProperty("confirmedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(0);
        live.GetProperty("rejectedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(1);
        live.GetProperty("confirmedCount").GetInt32().Should().Be(1);
        live.GetProperty("rejectedCount").GetInt32().Should().Be(1);
        live.GetProperty("playersCount").GetInt32().Should().Be(1);

        // Блокировка, подтверждение и отклонение — каждый раз свежие live-данные
        _hub.CallsFor("EventLiveUpdated").Should().HaveCount(3);
    }

    [Fact]
    public async Task RejectCell_BlocksConfirmAndKeepsSelection()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await LockAsync(session.Id);

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

        // Выбор зафиксирован: отклонение не освобождает слот
        JsonElement state = Json(await _service.GetPlayerStateAsync(session.Id, player));
        state.GetProperty("selectedCount").GetInt32().Should().Be(1);
        state.GetProperty("rejectedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(0);
    }

    [Fact]
    public async Task RemoveMark_BeforeLock_RemovesSelection()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await SeedMarkAsync(session.Id, player, 1, Start);

        BingoConfirmOutcome outcome = await _service.RemoveMarkAsync(session.Id, player, 0);

        outcome.Success.Should().BeTrue();
        Json(outcome.Data).GetProperty("selectedCount").GetInt32().Should().Be(1);

        JsonElement state = Json(await _service.GetPlayerStateAsync(session.Id, player));
        state.GetProperty("markedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(1);
        state.GetProperty("selectedCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task RemoveMark_AfterLock_IsRejected()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();
        await SeedMarkAsync(session.Id, player, 0, Start);
        await LockAsync(session.Id);

        BingoConfirmOutcome outcome = await _service.RemoveMarkAsync(session.Id, player, 0);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("закрыт");
    }

    [Fact]
    public async Task RemoveMark_NotMarked_IsRejected()
    {
        (_, EventSession session, Guid player) = await SeedEventAsync();

        BingoConfirmOutcome outcome = await _service.RemoveMarkAsync(session.Id, player, 0);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("не отмечена");
    }

    [Fact]
    public async Task ConfirmCell_ThenReject_IsRejected()
    {
        (_, EventSession session, _) = await SeedEventAsync();
        await LockAsync(session.Id);

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
        await LockAsync(session.Id);

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
        await LockAsync(session.Id);

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
    public async Task AdminState_ShowsMarksConfirmationsAndLock()
    {
        (_, EventSession session, Guid first) = await SeedEventAsync();
        Guid second = await SeedSecondPlayerAsync();

        await SeedMarkAsync(session.Id, first, 0, Start);
        await SeedMarkAsync(session.Id, second, 0, Start);
        await SeedMarkAsync(session.Id, first, 4, Start);
        await LockAsync(session.Id);
        await _service.ConfirmCellAsync(session.Id, 4);

        JsonElement state = Json(await _service.GetAdminStateAsync(session.Id));

        state.GetProperty("locked").GetBoolean().Should().BeTrue();
        state.GetProperty("playersCount").GetInt32().Should().Be(2);
        state.GetProperty("pickedPredictions").GetInt32().Should().Be(3);
        state.GetProperty("confirmedCells").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(4);
        state.GetProperty("markCounts").GetProperty("0").GetInt32().Should().Be(2);
        state.GetProperty("markCounts").GetProperty("4").GetInt32().Should().Be(1);
        state.GetProperty("maxPredictions").GetInt32().Should().Be(5); // половина поля 3×3, округление вверх
    }

    [Fact]
    public async Task AdminState_ClampsMaxPredictionsToHalfOfBoard()
    {
        const string configWithBigLimit = """
            {"size":3,"maxPredictions":99,"cells":["1","2","3","4","5","6","7","8","9"]}
            """;
        (_, EventSession session, _) = await SeedEventAsync(configWithBigLimit);

        JsonElement state = Json(await _service.GetAdminStateAsync(session.Id));

        state.GetProperty("maxPredictions").GetInt32().Should().Be(5);
    }
}
