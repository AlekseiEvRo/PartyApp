using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class EmojiSongHandlerTests : IDisposable
{
    private const string Config = """
        {
            "pointsPerCorrect": 5,
            "songs": [
                { "emoji": "🌞🌻", "answer": "Солнечный круг", "hint": "Детская песня" },
                { "emoji": "🎄", "answer": "В лесу родилась ёлочка", "hint": "Новый год" }
            ]
        }
        """;

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly SqliteTestHost _host = new();
    private readonly EmojiSongHandler _handler;

    public EmojiSongHandlerTests()
    {
        _handler = new EmojiSongHandler(_host.ScopeFactory, _award, NullLogger<EmojiSongHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private static (EventDefinition Definition, EventSession Session) CreateEvent(string configJson = Config)
    {
        EventDefinition definition = TestData.Definition("emoji_song", configJson);
        EventSession session = TestData.Session(definition, Guid.NewGuid());
        return (definition, session);
    }

    /// <summary>Сохраняет игрока, определение и сессию — нужны для FK при подсеве сабмитов.</summary>
    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync(
        string configJson = Config)
    {
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("emoji_song", configJson);
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    [Fact]
    public async Task CorrectAnswer_IsNormalizedAndAwarded()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();
        Guid playerId = Guid.NewGuid();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"songIndex":0,"answer":"  солнечный   КРУГ! "}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(5);
        await _award.Received(1).AwardAsync(
            playerId, 5, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task YoAndYe_AreTreatedTheSame()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"songIndex":1,"answer":"В лесу родилась елочка"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task WrongAnswer_FailsAndRevealsAnswer()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"songIndex":0,"answer":"Катюша"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Солнечный круг");
    }

    [Fact]
    public async Task SecondAttempt_OnSameSong_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = playerId,
            PayloadJson = """{"songIndex":0,"answer":"мимо"}""",
            Score = 0
        });
        await _host.Db.SaveChangesAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"songIndex":0,"answer":"Солнечный круг"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("уже отвечал");
    }

    [Fact]
    public async Task InvalidIndexOrEmptyAnswer_IsRejected()
    {
        (EventDefinition definition, EventSession session) = CreateEvent();

        SubmissionResult badIndex = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"songIndex":5,"answer":"тест"}""");
        SubmissionResult empty = await _handler.HandleSubmissionAsync(
            session, definition, Guid.NewGuid(), """{"songIndex":0,"answer":"   "}""");

        badIndex.Success.Should().BeFalse();
        empty.Success.Should().BeFalse();
    }

    [Fact]
    public async Task GetPlayerData_ListsAnsweredSongs()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = playerId,
            PayloadJson = """{"songIndex":0,"answer":"мимо"}""",
            Score = 0
        });
        await _host.Db.SaveChangesAsync();

        object? data = await _handler.GetPlayerDataAsync(session, definition, playerId);
        System.Text.Json.JsonElement json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(data));

        json.GetProperty("answered").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(0);
    }
}