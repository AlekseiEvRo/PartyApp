using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class QuickCheckinHandlerTests
{
    private const string ConfigJson = """{"points":1,"cooldownSeconds":30}""";

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly QuickCheckinHandler _handler;

    public QuickCheckinHandlerTests()
    {
        _handler = new QuickCheckinHandler(_award, _timeProvider, NullLogger<QuickCheckinHandler>.Instance);
    }

    private static (EventDefinition Definition, EventSession Session) CreateEvent(
        string configJson = ConfigJson,
        string displayName = "Тост за именинника")
    {
        EventDefinition definition = TestData.Definition("quick_checkin", configJson, displayName: displayName);
        EventSession session = TestData.Session(definition, Guid.NewGuid());
        return (definition, session);
    }

    [Fact]
    public async Task HandleSubmission_FirstToast_AwardsPoints()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        Guid playerId = Guid.NewGuid();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"playerName":"Алиса"}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(1);
        result.Message.Should().Be("Тост засчитан!");

        await _award.Received(1).AwardAsync(
            playerId,
            1,
            "Тост за именинника (Тост за именинника)",
            WalletTransactionType.EventReward,
            session.Id,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSubmission_SecondToastDuringCooldown_Fails()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        Guid first = Guid.NewGuid();

        await _handler.HandleSubmissionAsync(session, definition, first, """{"playerName":"Алиса"}""");
        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"playerName":"Борис"}""");

        result.Success.Should().BeFalse();
        result.PointsAwarded.Should().Be(0);
        result.Message.Should().StartWith("Сейчас говорит тост Алиса")
            .And.EndWith("Подожди 30 сек.");
        await _award.Received(1).AwardAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSubmission_AfterCooldownExpires_AllowsNextToast()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();

        await _handler.HandleSubmissionAsync(session, definition, Guid.NewGuid(), """{"playerName":"Алиса"}""");
        _timeProvider.Advance(TimeSpan.FromSeconds(31));

        Guid secondPlayer = Guid.NewGuid();
        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, secondPlayer, """{"playerName":"Борис"}""");

        result.Success.Should().BeTrue();
        await _award.Received(1).AwardAsync(
            secondPlayer, 1, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSubmission_AtExactCooldownBoundary_IsAllowed()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();

        await _handler.HandleSubmissionAsync(session, definition, Guid.NewGuid(), """{"playerName":"Алиса"}""");
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"playerName":"Борис"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_UsesCustomPointsAndCooldown()
    {
        (EventDefinition definition, EventSession session) = CreateEvent(
            """{"points":7,"cooldownSeconds":10}""");

        await _handler.HandleSubmissionAsync(session, definition, Guid.NewGuid(), """{"playerName":"Алиса"}""");
        SubmissionResult blocked = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"playerName":"Борис"}""");

        blocked.Message.Should().Contain("Подожди 10 сек.");
        await _award.Received(1).AwardAsync(
            Arg.Any<Guid>(), 7, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSubmission_WithoutConfig_UsesDefaults()
    {
        (EventDefinition definition, EventSession session) = CreateEvent("{}");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), payloadJson: null!);

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(1);
    }

    [Fact]
    public async Task HandleSubmission_WithoutPlayerName_StillWorksAndMessageHasNoName()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();

        SubmissionResult first = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), "{}");
        SubmissionResult second = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), "{}");

        first.Success.Should().BeTrue();
        second.Message.Should().Be("Сейчас говорит тост . Подожди 30 сек.");
    }

    [Fact]
    public async Task HandleSubmission_WithBrokenPayload_IgnoresPlayerName()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), "not-json");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_WhenAwardFails_ExceptionPropagatesAndCooldownStays()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        _award.AwardAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("касса сломалась")));

        Func<Task> act = () => _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"playerName":"Алиса"}""");

        await act.Should().ThrowAsync<InvalidOperationException>();

        SubmissionResult second = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"playerName":"Борис"}""");
        second.Success.Should().BeFalse();
    }
}