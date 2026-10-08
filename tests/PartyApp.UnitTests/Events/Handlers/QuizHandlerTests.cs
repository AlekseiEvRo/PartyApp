using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class QuizHandlerTests : IDisposable
{
    private const string ConfigJson = """
        {
            "pointsPerCorrect": 10,
            "questions": [
                { "text": "Q1", "options": ["a", "b", "c"], "correctIndex": 2 },
                { "text": "Q2", "options": ["x", "y"], "correctIndex": 0 }
            ]
        }
        """;

    private readonly SqliteTestHost _host;
    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly RecordingHubContext _hub = new();
    private readonly QuizHandler _handler;

    public QuizHandlerTests()
    {
        _host = new SqliteTestHost();
        _handler = new QuizHandler(_host.ScopeFactory, _award, _hub, NullLogger<QuizHandler>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private async Task<(User Player, EventDefinition Definition, EventSession Session)> SeedAsync(
        string configJson = ConfigJson,
        params string[] playerSubmissionPayloads)
    {
        User admin = TestData.User("admin", UserRole.Admin);
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("quiz", configJson);
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

    private static JsonElement DataJson(SubmissionResult result)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;
    }

    [Fact]
    public async Task HandleSubmission_WithNoQuestions_Fails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync("{}");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"questionIndex":0,"answerIndex":0}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Квиз не настроен");
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Theory]
    [InlineData("""{"answerIndex":0}""")]
    [InlineData("""{"questionIndex":-1,"answerIndex":0}""")]
    [InlineData("""{"questionIndex":2,"answerIndex":0}""")]
    [InlineData("""{"questionIndex":99,"answerIndex":0}""")]
    [InlineData("not-json-at-all")]
    [InlineData("")]
    public async Task HandleSubmission_WithInvalidQuestionIndex_Fails(string payload)
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, player.Id, payload);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Неверный индекс вопроса");
    }

    [Theory]
    [InlineData("""{"questionIndex":0,"answerIndex":-1}""")]
    [InlineData("""{"questionIndex":0,"answerIndex":3}""")]
    [InlineData("""{"questionIndex":0}""")]
    public async Task HandleSubmission_WithInvalidAnswerIndex_Fails(string payload)
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, player.Id, payload);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Неверный индекс ответа");
    }

    [Fact]
    public async Task HandleSubmission_WithCorrectAnswer_AwardsPoints()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"questionIndex":0,"answerIndex":2}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(10);
        result.Message.Should().Be("Правильно! 🎉");

        JsonElement data = DataJson(result);
        data.GetProperty("isCorrect").GetBoolean().Should().BeTrue();
        data.GetProperty("correctIndex").GetInt32().Should().Be(2);
        data.GetProperty("questionIndex").GetInt32().Should().Be(0);
        data.GetProperty("points").GetInt32().Should().Be(10);

        await _award.Received(1).AwardAsync(
            player.Id,
            10,
            "Квиз: вопрос 1",
            WalletTransactionType.EventReward,
            session.Id,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSubmission_WithWrongAnswer_DoesNotAwardPoints()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"questionIndex":0,"answerIndex":0}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(0);
        result.Message.Should().Be("Неправильно 😔");
        DataJson(result).GetProperty("isCorrect").GetBoolean().Should().BeFalse();

        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task HandleSubmission_SecondQuestion_UsesQuestionNumberInDescription()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        await _handler.HandleSubmissionAsync(session, definition, player.Id, """{"questionIndex":1,"answerIndex":0}""");

        await _award.Received(1).AwardAsync(
            player.Id, 10, "Квиз: вопрос 2", Arg.Any<WalletTransactionType>(), session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSubmission_SameQuestionTwice_FailsWithAlreadyAnswered()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: """{"questionIndex":0,"answerIndex":2}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"questionIndex":0,"answerIndex":2}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Ты уже ответил на этот вопрос");
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task HandleSubmission_AnsweredOtherQuestion_IsAllowed()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: """{"questionIndex":0,"answerIndex":2}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"questionIndex":1,"answerIndex":0}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_OtherPlayerAnsweredSameQuestion_IsAllowed()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        User otherPlayer = TestData.User("other");
        _host.Db.Users.Add(otherPlayer);
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = otherPlayer.Id,
            PayloadJson = """{"questionIndex":0,"answerIndex":2}"""
        });
        await _host.Db.SaveChangesAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"questionIndex":0,"answerIndex":2}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_WithBrokenStoredPayload_IsIgnoredAndSubmissionProceeds()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: "not-json");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"questionIndex":0,"answerIndex":2}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_WithoutPointsPerCorrect_FallsBackToTen()
    {
        string config = """
            { "questions": [ { "text": "Q", "options": ["a", "b"], "correctIndex": 1 } ] }
            """;
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(config);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"questionIndex":0,"answerIndex":1}""");

        result.PointsAwarded.Should().Be(10);
    }

    [Fact]
    public async Task HandleSubmission_PayloadPropertyLookup_IsCaseSensitive()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"QuestionIndex":0,"AnswerIndex":2}""");

        // TryGetProperty ищет точное имя, поэтому клиент обязан слать camelCase.
        result.Success.Should().BeFalse();
        result.Message.Should().Be("Неверный индекс вопроса");
    }

    [Fact]
    public void GetPublicConfig_HidesCorrectIndexAndTimeLimitSec()
    {
        EventDefinition definition = TestData.Definition("quiz", """
            {
                "timeLimitSec": 120,
                "pointsPerCorrect": 10,
                "questions": [
                    { "text": "Q1", "options": ["a", "b"], "correctIndex": 1 }
                ]
            }
            """);

        JsonElement publicConfig = JsonSerializer.SerializeToElement(_handler.GetPublicConfig(definition));

        // Таймер экрана берётся только из endsAt сессии, а не из конфига
        publicConfig.TryGetProperty("timeLimitSec", out _).Should().BeFalse();
        publicConfig.GetProperty("pointsPerCorrect").GetInt32().Should().Be(10);

        JsonElement question = publicConfig.GetProperty("questions").EnumerateArray().Single();
        question.GetProperty("text").GetString().Should().Be("Q1");
        question.GetProperty("options").GetArrayLength().Should().Be(2);

        // Правильный ответ не должен утекать игрокам в GET /data
        question.TryGetProperty("correctIndex", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetLiveDataAsync_CountsAnsweredAllAndTotalPlayers()
    {
        User admin = TestData.User("admin", UserRole.Admin);
        User first = TestData.User("first");
        User second = TestData.User("second");
        User idle = TestData.User("idle");
        User banned = TestData.User("banned");
        banned.IsActive = false;

        EventDefinition definition = TestData.Definition("quiz", ConfigJson);
        EventSession session = TestData.Session(definition, admin.Id);

        _host.Db.Users.AddRange(admin, first, second, idle, banned);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        _host.Db.PlayerSubmissions.AddRange(
            // first ответил на оба вопроса (повтор счётчик не удваивает)
            Submission(session, first, """{"questionIndex":0,"answerIndex":2}"""),
            Submission(session, first, """{"questionIndex":0,"answerIndex":2}"""),
            Submission(session, first, """{"questionIndex":1,"answerIndex":0}"""),
            // second — только на первый
            Submission(session, second, """{"questionIndex":0,"answerIndex":1}"""),
            // битый payload не считается
            Submission(session, idle, "not-json"));
        await _host.Db.SaveChangesAsync();

        object? live = await _handler.GetLiveDataAsync(session, definition);
        JsonElement json = JsonSerializer.SerializeToElement(live);

        json.GetProperty("answeredAll").GetInt32().Should().Be(1);
        json.GetProperty("players").GetInt32().Should().Be(2);
        // admin и забаненный в общий счётчик не входят
        json.GetProperty("totalPlayers").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task AfterSubmissionAsync_BroadcastsLiveProgress()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: """{"questionIndex":0,"answerIndex":2}""");

        await _handler.AfterSubmissionAsync(session, definition, player.Id);

        RecordingHubContext.HubCall call = _hub.SingleCall("EventLiveUpdated");
        call.Target.Should().Be("all");

        JsonElement payload = JsonSerializer.SerializeToElement(call.Payload);
        payload.GetProperty("sessionId").GetGuid().Should().Be(session.Id);

        JsonElement live = payload.GetProperty("live");
        live.GetProperty("players").GetInt32().Should().Be(1);
        // Ответ пока только на один вопрос из двух — «все» ещё не пройдены
        live.GetProperty("answeredAll").GetInt32().Should().Be(0);
        live.GetProperty("totalPlayers").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetPlayerDataAsync_ReturnsAnswersWithResults()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: new[]
            {
                """{"questionIndex":0,"answerIndex":2}""", // верно: correctIndex = 2
                """{"questionIndex":1,"answerIndex":1}""", // неверно: correctIndex = 0
                """{"questionIndex":1,"answerIndex":1}"""  // повторная попытка — не дублируем
            });

        object? playerData = await _handler.GetPlayerDataAsync(session, definition, player.Id);
        JsonElement[] answers = JsonSerializer.SerializeToElement(playerData)
            .GetProperty("answers").EnumerateArray().ToArray();

        answers.Should().HaveCount(2);

        JsonElement first = answers.Single(a => a.GetProperty("questionIndex").GetInt32() == 0);
        first.GetProperty("answerIndex").GetInt32().Should().Be(2);
        first.GetProperty("isCorrect").GetBoolean().Should().BeTrue();
        first.GetProperty("correctIndex").GetInt32().Should().Be(2);

        JsonElement second = answers.Single(a => a.GetProperty("questionIndex").GetInt32() == 1);
        second.GetProperty("answerIndex").GetInt32().Should().Be(1);
        second.GetProperty("isCorrect").GetBoolean().Should().BeFalse();
        second.GetProperty("correctIndex").GetInt32().Should().Be(0);
    }

    private static PlayerSubmission Submission(EventSession session, User player, string payloadJson) => new()
    {
        SessionId = session.Id,
        PlayerId = player.Id,
        PayloadJson = payloadJson
    };
}