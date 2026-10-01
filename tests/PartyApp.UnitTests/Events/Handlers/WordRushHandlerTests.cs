using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class WordRushHandlerTests : IDisposable
{
    private const string ConfigJson = """
        {
            "requiredLetters": ["А", "Е"],
            "minWordLength": 3,
            "pointsPerWord": 5,
            "uniqueWordsOnly": true
        }
        """;

    private readonly SqliteTestHost _host;
    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly TempDictionaryFile _dictionary = new("тарелка", "аптека", "сеанс", "ёжик", "торт");
    private readonly WordRushHandler _handler;

    public WordRushHandlerTests()
    {
        _host = new SqliteTestHost();
        _handler = new WordRushHandler(
            _host.ScopeFactory,
            _dictionary.CreateService(),
            _award,
            NullLogger<WordRushHandler>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
        _dictionary.Dispose();
    }

    private async Task<(User Player, EventDefinition Definition, EventSession Session)> SeedAsync(
        string configJson = ConfigJson,
        params string[] playerSubmissionPayloads)
    {
        User admin = TestData.User("admin", UserRole.Admin);
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("word_rush", configJson);
        EventSession session = TestData.Session(definition, admin.Id);

        _host.Db.Users.AddRange(admin, player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);

        foreach (string payload in playerSubmissionPayloads)
        {
            _host.Db.PlayerSubmissions.Add(new PlayerSubmission
            {
                SessionId = session.Id,
                PlayerId = player.Id,
                PayloadJson = payload
            });
        }

        await _host.Db.SaveChangesAsync();
        return (player, definition, session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"word":"   "}""")]
    [InlineData("not-json")]
    [InlineData("""{"word":123}""")]
    public async Task HandleSubmission_WithoutWord_Fails(string payload)
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, player.Id, payload);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Введи слово");
    }

    [Fact]
    public async Task HandleSubmission_WithTooShortWord_Fails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"ае"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Слово должно быть минимум 3 буквы");
    }

    [Fact]
    public async Task HandleSubmission_WithoutRequiredLetter_Fails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"торт"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Слово должно содержать букву «А»");
    }

    [Fact]
    public async Task HandleSubmission_WithUnknownWord_Fails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"карамель"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("не найдено в словаре");
        result.Message.Should().Contain("карамель");
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task HandleSubmission_WithValidWord_AwardsPoints()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"тарелка"}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(5);
        result.Message.Should().Be("Слово «тарелка» засчитано!");

        await _award.Received(1).AwardAsync(
            player.Id,
            5,
            "Слово: тарелка",
            WalletTransactionType.EventReward,
            session.Id,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSubmission_IsCaseInsensitive()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"  ТАРЕЛКА  "}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_TreatsYoAndYeAsSameLetter()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            """
            { "requiredLetters": ["Е"], "minWordLength": 3, "pointsPerWord": 5 }
            """);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"ЕЖИК"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_RepeatedWord_FailsWhenUniqueWordsOnly()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: """{"word":"тарелка"}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"ТАРЕЛКА"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Ты уже использовал это слово");
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task HandleSubmission_RepeatedWordWithYo_IsDetectedAsDuplicate()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            """
            { "requiredLetters": ["Е"], "minWordLength": 3, "pointsPerWord": 5, "uniqueWordsOnly": true }
            """,
            playerSubmissionPayloads: """{"word":"ёжик"}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"ежик"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Ты уже использовал это слово");
    }

    [Fact]
    public async Task HandleSubmission_RepeatedWord_IsAllowedWhenUniquenessDisabled()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            """
            { "requiredLetters": ["А", "Е"], "minWordLength": 3, "pointsPerWord": 5, "uniqueWordsOnly": false }
            """,
            playerSubmissionPayloads: """{"word":"тарелка"}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"тарелка"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_WordUsedByOtherPlayer_IsAllowed()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        User otherPlayer = TestData.User("other");
        _host.Db.Users.Add(otherPlayer);
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = otherPlayer.Id,
            PayloadJson = """{"word":"тарелка"}"""
        });
        await _host.Db.SaveChangesAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"тарелка"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_WithBrokenStoredPayload_IsIgnored()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: "not-json");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"тарелка"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_WithoutConfig_UsesDefaults()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync("{}");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"тарелка"}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(5);
    }

    [Fact]
    public async Task HandleSubmission_CustomRequiredLettersAndPoints_AreApplied()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            """
            { "requiredLetters": ["О"], "minWordLength": 3, "pointsPerWord": 12 }
            """);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"word":"торт"}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(12);
    }
}