using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class PredictionsHandlerTests : IDisposable
{
    private const string Config = """{"points":3,"prompt":"Что случится на вечеринке?"}""";

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly SqliteTestHost _host = new();
    private readonly PredictionsHandler _handler;

    public PredictionsHandlerTests()
    {
        _handler = new PredictionsHandler(_host.ScopeFactory, _award, NullLogger<PredictionsHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync(
        string configJson = Config,
        string username = "player")
    {
        User player = TestData.User(username);
        EventDefinition definition = TestData.Definition("predictions", configJson);
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    private async Task SeedPredictionAsync(EventSession session, Guid playerId, string text)
    {
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = playerId,
            PayloadJson = JsonSerializer.Serialize(new { text }),
            Score = 3
        });
        await _host.Db.SaveChangesAsync();
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task FirstPrediction_IsAcceptedAndAwarded()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"text":"Будет торт"}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(3);

        await _award.Received(1).AwardAsync(
            playerId, 3, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SecondPrediction_IsRejectedWithOwnText()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();
        await SeedPredictionAsync(session, playerId, "Первое предсказание");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, """{"text":"Второе"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("уже");
        Json(result.Data).GetProperty("text").GetString().Should().Be("Первое предсказание");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyText_IsRejected(string text)
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId, JsonSerializer.Serialize(new { text }));

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LongText_IsTruncated()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, playerId,
            JsonSerializer.Serialize(new { text = new string('а', 250) }));

        result.Success.Should().BeTrue();
        Json(result.Data).GetProperty("text").GetString()!.Length.Should().Be(200);
    }

    [Fact]
    public async Task LiveData_HidesPredictionsUntilSessionFinishes()
    {
        (EventDefinition definition, EventSession session, Guid first) = await SeedEventAsync(username: "first");

        User second = TestData.User("second");
        _host.Db.Users.Add(second);
        await _host.Db.SaveChangesAsync();

        await SeedPredictionAsync(session, first, "Будет торт");
        await SeedPredictionAsync(session, second.Id, "Будет караоке");

        JsonElement active = Json(await _handler.GetLiveDataAsync(session, definition));
        active.GetProperty("count").GetInt32().Should().Be(2);
        active.GetProperty("revealed").ValueKind.Should().Be(JsonValueKind.Null);
        active.GetProperty("prompt").GetString().Should().NotBeNullOrWhiteSpace();

        session.State = EventSessionState.Finished;

        JsonElement finished = Json(await _handler.GetLiveDataAsync(session, definition));
        finished.GetProperty("revealed").GetArrayLength().Should().Be(2);
        finished.GetProperty("revealed")[0].GetProperty("playerName").GetString().Should().Be("first");
        finished.GetProperty("revealed")[0].GetProperty("text").GetString().Should().Be("Будет торт");
    }

    [Fact]
    public async Task PlayerData_ReturnsOwnPrediction()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        JsonElement empty = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));
        empty.GetProperty("text").ValueKind.Should().Be(JsonValueKind.Null);

        await SeedPredictionAsync(session, playerId, "Будет салют");

        JsonElement filled = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));
        filled.GetProperty("text").GetString().Should().Be("Будет салют");
    }
}