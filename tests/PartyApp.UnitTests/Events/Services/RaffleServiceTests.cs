using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Services;

public class RaffleServiceTests : IDisposable
{
    private readonly RecordingHubContext _hub = new();
    private readonly SqliteTestHost _host = new();
    private readonly RaffleService _service;

    public RaffleServiceTests()
    {
        _service = new RaffleService(_host.ScopeFactory, _hub, NullLogger<RaffleService>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private async Task<(EventSession Session, Guid PlayerId)> SeedEventAsync(string type = "raffle")
    {
        User player = TestData.User($"u-{Guid.NewGuid():N}"[..10]);
        EventDefinition definition = TestData.Definition(type, "{}");
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (session, player.Id);
    }

    private async Task<(Guid PlayerId, int? TicketNumber)> SeedParticipantAsync(
        EventSession session, int? ticketNumber)
    {
        User player = TestData.User($"u-{Guid.NewGuid():N}"[..10]);
        _host.Db.Users.Add(player);
        await _host.Db.SaveChangesAsync();

        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = player.Id,
            PayloadJson = "{}",
            TicketNumber = ticketNumber,
            Score = 0
        });
        await _host.Db.SaveChangesAsync();

        return (player.Id, ticketNumber);
    }

    private async Task AddSubmissionAsync(EventSession session, Guid playerId, int? ticketNumber)
    {
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = playerId,
            PayloadJson = "{}",
            TicketNumber = ticketNumber,
            Score = 0
        });
        await _host.Db.SaveChangesAsync();
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task Draw_PicksTicketAndBroadcasts()
    {
        (EventSession session, _) = await SeedEventAsync();
        (Guid first, int? firstTicket) = await SeedParticipantAsync(session, 111);
        (Guid second, int? secondTicket) = await SeedParticipantAsync(session, 222);

        RaffleDrawOutcome outcome = await _service.DrawAsync(session.Id);

        outcome.Success.Should().BeTrue();

        RaffleDraw draw = await _host.DbAsync(db => db.RaffleDraws.AsNoTracking()
            .SingleAsync(d => d.SessionId == session.Id));

        draw.WinnerTicketNumber.Should().NotBeNull();
        new int?[] { firstTicket, secondTicket }.Should().Contain(draw.WinnerTicketNumber);
        draw.WinnerId.Should().Be(draw.WinnerTicketNumber == firstTicket ? first : second);

        // В общую рассылку уходят только номера — имён игроков там нет
        JsonElement payload = Json(_hub.SingleCall("RaffleDrawn").Payload);
        payload.GetProperty("winnerTicket").GetInt32().Should().Be(draw.WinnerTicketNumber);
        payload.GetProperty("ticketsCount").GetInt32().Should().Be(2);
        payload.TryGetProperty("winner", out _).Should().BeFalse();
        payload.TryGetProperty("participants", out _).Should().BeFalse();

        // Имя победителя видит только админ в ответе на розыгрыш
        JsonElement adminData = Json(outcome.Data);
        adminData.GetProperty("winnerTicket").GetInt32().Should().Be(draw.WinnerTicketNumber);
        adminData.GetProperty("winnerName").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Draw_BackfillsLegacyTickets()
    {
        (EventSession session, _) = await SeedEventAsync();
        await SeedParticipantAsync(session, ticketNumber: null);
        await SeedParticipantAsync(session, ticketNumber: null);

        RaffleDrawOutcome outcome = await _service.DrawAsync(session.Id);

        outcome.Success.Should().BeTrue();

        List<PlayerSubmission> submissions = await _host.DbAsync(db => db.PlayerSubmissions.AsNoTracking()
            .Where(s => s.SessionId == session.Id)
            .ToListAsync());

        submissions.Should().OnlyContain(s => s.TicketNumber != null);
        submissions.Select(s => s.TicketNumber!.Value).Should().OnlyHaveUniqueItems();
        submissions.Select(s => s.TicketNumber!.Value)
            .Should().OnlyContain(n => n >= 1 && n <= RaffleService.MaxTicketNumber);

        RaffleDraw draw = await _host.DbAsync(db => db.RaffleDraws.AsNoTracking()
            .SingleAsync(d => d.SessionId == session.Id));

        draw.WinnerTicketNumber.Should().BeOneOf(submissions.Select(s => s.TicketNumber!.Value));
    }

    [Fact]
    public async Task Draw_IgnoresFailedSubmissionsWithoutTickets()
    {
        (EventSession session, _) = await SeedEventAsync();
        (Guid participant, int? ticket) = await SeedParticipantAsync(session, 555);

        // Отклонённая повторная попытка сохраняется без номера — это не билет
        await AddSubmissionAsync(session, participant, null);

        RaffleDrawOutcome outcome = await _service.DrawAsync(session.Id);

        outcome.Success.Should().BeTrue();

        JsonElement data = Json(outcome.Data);
        data.GetProperty("winnerTicket").GetInt32().Should().Be(ticket!.Value);
        data.GetProperty("ticketsCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Draw_Twice_IsRejected()
    {
        (EventSession session, _) = await SeedEventAsync();
        await SeedParticipantAsync(session, 10);

        await _service.DrawAsync(session.Id);
        RaffleDrawOutcome second = await _service.DrawAsync(session.Id);

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже проводился");
    }

    [Fact]
    public async Task Draw_WithoutParticipants_IsRejected()
    {
        (EventSession session, _) = await SeedEventAsync();

        RaffleDrawOutcome outcome = await _service.DrawAsync(session.Id);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("никто");
    }

    [Fact]
    public async Task Draw_NonRaffleSession_IsRejected()
    {
        (EventSession session, _) = await SeedEventAsync(type: "quiz");

        RaffleDrawOutcome outcome = await _service.DrawAsync(session.Id);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("не найдена");
    }

    [Fact]
    public async Task State_ReturnsTicketsAndWinnerWithoutNames()
    {
        (EventSession session, _) = await SeedEventAsync();
        const int ticket = 321;
        await SeedParticipantAsync(session, ticket);

        JsonElement before = Json(await _service.GetStateAsync(session.Id));
        before.GetProperty("tickets").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(ticket);
        before.GetProperty("ticketsCount").GetInt32().Should().Be(1);
        before.GetProperty("playersCount").GetInt32().Should().Be(1);
        before.GetProperty("winnerTicket").ValueKind.Should().Be(JsonValueKind.Null);
        before.TryGetProperty("participants", out _).Should().BeFalse();

        await _service.DrawAsync(session.Id);

        JsonElement after = Json(await _service.GetStateAsync(session.Id));
        after.GetProperty("winnerTicket").GetInt32().Should().Be(ticket);
        after.TryGetProperty("winner", out _).Should().BeFalse();
    }

    [Fact]
    public void PickFreeTicketNumber_SkipsUsedNumbers()
    {
        int? number = RaffleService.PickFreeTicketNumber(new[] { 1, 2, 3, 4, 5 });

        number.Should().NotBeNull();
        number!.Value.Should().BeInRange(1, RaffleService.MaxTicketNumber);
        new[] { 1, 2, 3, 4, 5 }.Should().NotContain(number.Value);
    }

    [Fact]
    public void PickFreeTicketNumber_AllNumbersUsed_ReturnsNull()
    {
        IEnumerable<int> all = Enumerable.Range(1, RaffleService.MaxTicketNumber);

        RaffleService.PickFreeTicketNumber(all).Should().BeNull();
    }
}

public class RaffleHandlerTests : IDisposable
{
    private readonly RecordingHubContext _hub = new();
    private readonly SqliteTestHost _host = new();
    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly RaffleHandler _handler;

    public RaffleHandlerTests()
    {
        RaffleService service = new(_host.ScopeFactory, _hub, NullLogger<RaffleService>.Instance);
        _handler = new RaffleHandler(_host.ScopeFactory, service, _award, NullLogger<RaffleHandler>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private async Task<(EventDefinition Definition, EventSession Session, Guid PlayerId)> SeedEventAsync(
        string config = """{"prize":"Приз"}""")
    {
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("raffle", config);
        EventSession session = TestData.Session(definition, player.Id);

        _host.Db.Users.Add(player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session, player.Id);
    }

    /// <summary>Симулирует сохранение заявки: в проде это делает EventService.</summary>
    private async Task PersistJoinAsync(EventSession session, Guid playerId, SubmissionResult result)
    {
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = playerId,
            PayloadJson = "{}",
            TicketNumber = result.TicketNumber,
            Score = 0
        });
        await _host.Db.SaveChangesAsync();
    }

    private async Task SeedTicketAsync(EventSession session, Guid playerId, int ticketNumber)
    {
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = playerId,
            PayloadJson = "{}",
            TicketNumber = ticketNumber,
            Score = 0
        });
        await _host.Db.SaveChangesAsync();
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task Join_GivesTicketNumberWithoutPoints()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(0);
        result.TicketNumber.Should().NotBeNull();
        result.TicketNumber!.Value.Should().BeInRange(1, RaffleService.MaxTicketNumber);

        JsonElement data = Json(result.Data);
        data.GetProperty("joined").GetBoolean().Should().BeTrue();
        data.GetProperty("ticketNumber").GetInt32().Should().Be(result.TicketNumber);
        data.GetProperty("myTickets").EnumerateArray().Select(e => e.GetInt32())
            .Should().Equal(result.TicketNumber!.Value);
        data.GetProperty("maxTickets").GetInt32().Should().Be(1);
        data.GetProperty("nextTicketPrice").GetInt32().Should().Be(0);

        // Первый билет бесплатный — кошелёк не трогаем
        _award.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Join_Twice_IsRejectedByDefaultLimit()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync();

        SubmissionResult first = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
        await PersistJoinAsync(session, playerId, first);

        SubmissionResult second = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже участвуешь");
    }

    [Fact]
    public async Task Join_SecondTicket_ChargesPoints()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync(
            """{"prize":"Приз","ticketPrice":10,"maxTickets":3}""");

        _award.TrySpendAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(90);

        SubmissionResult first = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
        await PersistJoinAsync(session, playerId, first);

        SubmissionResult second = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        second.Success.Should().BeTrue();
        second.TicketNumber.Should().NotBe(first.TicketNumber);
        Json(second.Data).GetProperty("nextTicketPrice").GetInt32().Should().Be(10);

        await _award.Received(1).TrySpendAsync(
            playerId, 10, "Билет лототрона", WalletTransactionType.RaffleTicket,
            session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Join_SecondTicket_WithoutBalance_IsRejected()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync(
            """{"prize":"Приз","ticketPrice":10,"maxTickets":3}""");

        _award.TrySpendAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);

        SubmissionResult first = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
        await PersistJoinAsync(session, playerId, first);

        SubmissionResult second = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("Не хватает баллов");
    }

    [Fact]
    public async Task Join_EnforcesMaxTickets()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync(
            """{"prize":"Приз","ticketPrice":0,"maxTickets":2}""");

        SubmissionResult first = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
        await PersistJoinAsync(session, playerId, first);

        SubmissionResult second = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
        await PersistJoinAsync(session, playerId, second);

        SubmissionResult third = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");

        third.Success.Should().BeFalse();
        third.Message.Should().Contain("Лимит");
    }

    [Fact]
    public async Task Join_TicketsAreUniqueWithinSession()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync(
            """{"prize":"Приз","ticketPrice":0,"maxTickets":5}""");

        var numbers = new List<int?>();

        for (int i = 0; i < 5; i++)
        {
            SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, playerId, "{}");
            result.Success.Should().BeTrue();
            numbers.Add(result.TicketNumber);
            await PersistJoinAsync(session, playerId, result);
        }

        numbers.Should().OnlyHaveUniqueItems();
        numbers.Should().OnlyContain(n => n >= 1 && n <= RaffleService.MaxTicketNumber);
    }

    [Fact]
    public async Task PlayerData_ReflectsTicketsAndNextPrice()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync(
            """{"prize":"Приз","ticketPrice":7,"maxTickets":3}""");

        User stranger = TestData.User("stranger");
        _host.Db.Users.Add(stranger);
        await _host.Db.SaveChangesAsync();

        await SeedTicketAsync(session, playerId, 11);
        await SeedTicketAsync(session, playerId, 22);
        await SeedTicketAsync(session, stranger.Id, 33);

        JsonElement data = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));

        data.GetProperty("joined").GetBoolean().Should().BeTrue();
        data.GetProperty("myTickets").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(11, 22);
        data.GetProperty("maxTickets").GetInt32().Should().Be(3);
        data.GetProperty("nextTicketPrice").GetInt32().Should().Be(7);
        data.GetProperty("playersCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task PlayerData_NotJoined_HasFreeFirstTicket()
    {
        (EventDefinition definition, EventSession session, Guid playerId) = await SeedEventAsync(
            """{"prize":"Приз","ticketPrice":7,"maxTickets":3}""");

        JsonElement data = Json(await _handler.GetPlayerDataAsync(session, definition, playerId));

        data.GetProperty("joined").GetBoolean().Should().BeFalse();
        data.GetProperty("myTickets").GetArrayLength().Should().Be(0);
        data.GetProperty("nextTicketPrice").GetInt32().Should().Be(0);
    }
}
