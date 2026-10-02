using System.Text.Json;

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

public class ReactionHandlerTests : IDisposable
{
    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestHost _host = new();
    private readonly ReactionHandler _handler;

    public ReactionHandlerTests()
    {
        _handler = new ReactionHandler(
            _host.ScopeFactory, _award, _timeProvider, NullLogger<ReactionHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private static (EventDefinition Definition, EventSession Session) CreateEvent(
        string configJson = """{"delaySec":5,"timeLimitSec":15,"points":10}""")
    {
        EventDefinition definition = TestData.Definition("reaction", configJson);
        EventSession session = TestData.Session(definition, Guid.NewGuid());
        return (definition, session);
    }

    /// <summary>Сохраняет игрока, определение и сессию — нужны для FK при подсеве сабмитов.</summary>
    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync()
    {
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("reaction");
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    [Fact]
    public async Task BeforeSignal_IsRejected()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        session.StartedAt = _timeProvider.GetUtcNow().UtcDateTime;

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), "{}");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Рано");
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task AfterSignal_AwardsMaxPointsForInstantReaction()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        session.StartedAt = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-5);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), "{}");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(10);
        result.DurationMs.Should().NotBeNull();
        result.DurationMs!.Value.Should().BeInRange(0, 50);
    }

    [Fact]
    public async Task SlowerReaction_GetsPenalty()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        session.StartedAt = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-5);

        _timeProvider.Advance(TimeSpan.FromSeconds(1.2)); // ~4 шага по 300 мс

        Guid playerId = Guid.NewGuid();
        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, "{}");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().BeInRange(5, 7);
        await _award.Received(1).AwardAsync(
            playerId, result.PointsAwarded, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TooLate_IsRejected()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        session.StartedAt = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-25);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), "{}");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("поздно");
    }

    [Fact]
    public async Task SecondReaction_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();
        session.StartedAt = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-5);

        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = playerId,
            PayloadJson = "{}",
            Score = 10
        });
        await _host.Db.SaveChangesAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, "{}");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("уже");
    }

    [Fact]
    public async Task GetPlayerData_ReportsReactionState()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        object? before = await _handler.GetPlayerDataAsync(session, definition, playerId);
        Json(before).GetProperty("reacted").GetBoolean().Should().BeFalse();

        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id, PlayerId = playerId, PayloadJson = "{}", Score = 3
        });
        await _host.Db.SaveChangesAsync();

        object? after = await _handler.GetPlayerDataAsync(session, definition, playerId);
        Json(after).GetProperty("reacted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetLiveData_ReturnsSortedResultsAndWindow()
    {
        (EventDefinition definition, EventSession session, Guid first) = await SeedEventAsync();
        session.StartedAt = _timeProvider.GetUtcNow().UtcDateTime;

        User second = TestData.User("second");
        _host.Db.Users.Add(second);
        await _host.Db.SaveChangesAsync();

        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id, PlayerId = first, PayloadJson = "{}", Score = 4, DurationMs = 640
        });
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id, PlayerId = second.Id, PayloadJson = "{}", Score = 9, DurationMs = 120
        });
        await _host.Db.SaveChangesAsync();

        JsonElement live = Json(await _handler.GetLiveDataAsync(session, definition));

        live.GetProperty("reactedCount").GetInt32().Should().Be(2);
        live.GetProperty("startsAtUtc").GetDateTime().Should()
            .BeCloseTo(session.StartedAt.AddSeconds(5), TimeSpan.FromSeconds(1));
        live.GetProperty("endsAtUtc").GetDateTime().Should()
            .BeCloseTo(session.StartedAt.AddSeconds(20), TimeSpan.FromSeconds(1));

        JsonElement results = live.GetProperty("results");
        results[0].GetProperty("playerName").GetString().Should().Be("second");
        results[0].GetProperty("elapsedMs").GetInt32().Should().Be(120);
        results[0].GetProperty("points").GetInt32().Should().Be(9);
        results[1].GetProperty("elapsedMs").GetInt32().Should().Be(640);
    }

    private static System.Text.Json.JsonElement Json(object? value)
    {
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(value));
    }
}