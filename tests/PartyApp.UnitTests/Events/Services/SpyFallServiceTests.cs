using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Services;

public class SpyFallServiceTests : IDisposable
{
    private const string SmallConfig = """
        {
            "pointsSpy": 30,
            "pointsCitizen": 10,
            "pairs": [
                { "citizen": "торт", "spy": "пирожное" },
                { "citizen": "чай", "spy": "кофе" },
                { "citizen": "кот", "spy": "собака" }
            ]
        }
        """;

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly RecordingHubContext _hub = new();
    private readonly FakePushNotificationService _push = new();
    private readonly SqliteTestHost _host = new();
    private readonly SpyFallService _service;
    private readonly Guid _adminId;

    public SpyFallServiceTests()
    {
        _service = new SpyFallService(
            _host.ScopeFactory, _award, _hub, _push, NullLogger<SpyFallService>.Instance);

        User admin = TestData.User("admin", UserRole.Admin);
        _host.Db.Users.Add(admin);
        _host.Db.SaveChanges();
        _adminId = admin.Id;
    }

    public void Dispose() => _host.Dispose();

    private async Task<(EventDefinition Definition, List<User> Players)> SeedEventAsync(
        int playerCount = 4,
        string configJson = SmallConfig,
        UserRole role = UserRole.Player)
    {
        List<User> players = Enumerable.Range(0, playerCount)
            .Select(i => TestData.User($"p{i}-{Guid.NewGuid():N}"[..14], role))
            .ToList();

        EventDefinition definition = TestData.Definition("spyfall", configJson);

        _host.Db.Users.AddRange(players);
        _host.Db.EventDefinitions.Add(definition);
        await _host.Db.SaveChangesAsync();

        return (definition, players);
    }

    private Task<SpyFallOutcome> StartAsync(EventDefinition definition, IEnumerable<User> players)
    {
        return _service.StartAsync(definition.Id, _adminId, players.Select(p => p.Id).ToList());
    }

    private async Task<Guid> StartGameAsync(EventDefinition definition, IEnumerable<User> players)
    {
        SpyFallOutcome outcome = await StartAsync(definition, players);
        outcome.Success.Should().BeTrue(outcome.Message);
        return Json(outcome.Data).GetProperty("sessionId").GetGuid();
    }

    private async Task<(SpyFallRound Round, List<SpyFallParticipant> Participants)> LoadRoundAsync(Guid sessionId)
    {
        return await _host.DbAsync(async db =>
        {
            SpyFallRound round = await db.SpyFallRounds.AsNoTracking()
                .SingleAsync(r => r.SessionId == sessionId);

            List<SpyFallParticipant> participants = await db.SpyFallParticipants.AsNoTracking()
                .Where(p => p.SessionId == sessionId)
                .ToListAsync();

            return (round, participants);
        });
    }

    private static JsonElement Json(object? value)
    {
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
    }

    // === Запуск ===

    [Fact]
    public async Task Start_WithFewerThanFourPlayers_IsRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync(3);

        SpyFallOutcome outcome = await StartAsync(definition, players);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("минимум 4");
        (await _host.DbAsync(db => db.EventSessions.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Start_WithDuplicates_IsRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync(4);
        players.Add(players[0]);

        SpyFallOutcome outcome = await StartAsync(definition, players);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("повторяться");
    }

    [Fact]
    public async Task Start_WithNonPlayerRole_IsRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync(4, role: UserRole.Admin);

        SpyFallOutcome outcome = await StartAsync(definition, players);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("только игроки");
    }

    [Fact]
    public async Task Start_WithTooFewPairs_IsRejected()
    {
        const string config = """
            {"pointsSpy":10,"pointsCitizen":5,"pairs":[{"citizen":"торт","spy":"пирожное"}]}
            """;
        (EventDefinition definition, List<User> players) = await SeedEventAsync(configJson: config);

        SpyFallOutcome outcome = await StartAsync(definition, players);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("3 пары");
    }

    [Fact]
    public async Task Start_DealsOneSpyAndNotifiesParticipantsOnly()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();

        Guid sessionId = await StartGameAsync(definition, players);

        (SpyFallRound round, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);

        participants.Should().HaveCount(4);
        participants.Count(p => p.IsSpy).Should().Be(1);
        round.SpyUserId.Should().Be(participants.Single(p => p.IsSpy).UserId);

        // Пара выдана целиком, но стороны могли поменяться местами
        string[] pair = new[] { "торт", "пирожное", "чай", "кофе", "кот", "собака" };
        pair.Should().Contain(round.CitizenWord);
        pair.Should().Contain(round.SpyWord);
        round.CitizenWord.Should().NotBe(round.SpyWord);

        // EventStarted уходит всем, но с participantIds — чужие клиенты его игнорируют
        JsonElement payload = Json(_hub.SingleCall("EventStarted").Payload);
        payload.GetProperty("participantIds").GetArrayLength().Should().Be(4);
        payload.GetProperty("type").GetString().Should().Be("spyfall");

        // Push — только участникам
        PushCall push = _push.Calls.Should().ContainSingle().Subject;
        push.UserIds.Should().BeEquivalentTo(players.Select(p => p.Id));
    }

    [Fact]
    public async Task Start_WhileAnotherGameIsActive_IsRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        await StartGameAsync(definition, players);

        SpyFallOutcome second = await StartAsync(definition, players);

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже запущена");
    }

    // === Голосование ===

    [Fact]
    public async Task Vote_BySpy_IsRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        Guid citizenId = participants.First(p => !p.IsSpy).UserId;

        SpyFallOutcome outcome = await _service.VoteAsync(sessionId, round.SpyUserId, citizenId);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("Шпион не голосует");
    }

    [Fact]
    public async Task Vote_ForSelf_IsRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (_, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        Guid citizenId = participants.First(p => !p.IsSpy).UserId;

        SpyFallOutcome outcome = await _service.VoteAsync(sessionId, citizenId, citizenId);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("за себя");
    }

    [Fact]
    public async Task Vote_ForOutsider_IsRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (_, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        Guid citizenId = participants.First(p => !p.IsSpy).UserId;

        SpyFallOutcome outcome = await _service.VoteAsync(sessionId, citizenId, Guid.NewGuid());

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("не участвует");
    }

    [Fact]
    public async Task Vote_CanBeChangedBeforeLastVote()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        List<SpyFallParticipant> citizens = participants.Where(p => !p.IsSpy).ToList();

        await _service.VoteAsync(sessionId, citizens[0].UserId, citizens[1].UserId);
        SpyFallOutcome changed = await _service.VoteAsync(sessionId, citizens[0].UserId, round.SpyUserId);

        changed.Success.Should().BeTrue();
        changed.Message.Should().Contain("изменён");

        (_, List<SpyFallParticipant> after) = await LoadRoundAsync(sessionId);
        after.Single(p => p.UserId == citizens[0].UserId).VotedForId.Should().Be(round.SpyUserId);
    }

    // === Итог голосования ===

    [Fact]
    public async Task AllCitizensVoted_SpyHasMostVotes_CitizensWinAndOnlyCorrectVotesPay()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        List<SpyFallParticipant> citizens = participants.Where(p => !p.IsSpy).ToList();

        // Двое против шпиона, один голосует за другого горожанина
        await _service.VoteAsync(sessionId, citizens[0].UserId, round.SpyUserId);
        await _service.VoteAsync(sessionId, citizens[1].UserId, citizens[2].UserId);
        SpyFallOutcome last = await _service.VoteAsync(sessionId, citizens[2].UserId, round.SpyUserId);

        last.Success.Should().BeTrue();

        (SpyFallRound resolved, _) = await LoadRoundAsync(sessionId);
        resolved.Winner.Should().Be("citizens");
        resolved.ResolvedBy.Should().Be("votes");

        // Баллы — только тем, кто угадал
        await _award.Received(1).AwardAsync(
            citizens[0].UserId, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.Received(1).AwardAsync(
            citizens[2].UserId, 10, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.DidNotReceive().AwardAsync(
            citizens[1].UserId, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.DidNotReceive().AwardAsync(
            round.SpyUserId, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        JsonElement live = Json(_hub.CallsFor("EventLiveUpdated").Last().Payload).GetProperty("live");
        live.GetProperty("phase").GetString().Should().Be("finished");
        live.GetProperty("winner").GetString().Should().Be("citizens");
        live.GetProperty("spyName").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AllCitizensVoted_TieOrOtherLeader_SpyWinsAndGetsPoints()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        List<SpyFallParticipant> citizens = participants.Where(p => !p.IsSpy).ToList();

        // Шпион не лидирует: каждый набрал по голосу (ничья), значит победа шпиона
        await _service.VoteAsync(sessionId, citizens[0].UserId, round.SpyUserId);
        await _service.VoteAsync(sessionId, citizens[1].UserId, citizens[2].UserId);
        await _service.VoteAsync(sessionId, citizens[2].UserId, citizens[1].UserId);

        (SpyFallRound resolved, _) = await LoadRoundAsync(sessionId);
        resolved.Winner.Should().Be("spy");

        await _award.Received(1).AwardAsync(
            round.SpyUserId, 30, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _award.DidNotReceive().AwardAsync(
            citizens[0].UserId, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    // === Попытка шпиона ===

    [Fact]
    public async Task Guess_CorrectWord_SpyWinsImmediately()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, _) = await LoadRoundAsync(sessionId);

        // Регистр и ё/е не важны
        SpyFallOutcome outcome = await _service.GuessAsync(
            sessionId, round.SpyUserId, $"  {round.CitizenWord.ToUpperInvariant().Replace('Е', 'Ё')}  ");

        outcome.Success.Should().BeTrue();
        outcome.PointsAwarded.Should().Be(30);
        outcome.Message.Should().Contain("угадал");

        (SpyFallRound resolved, _) = await LoadRoundAsync(sessionId);
        resolved.Winner.Should().Be("spy");
        resolved.ResolvedBy.Should().Be("guess");
        resolved.GuessCorrect.Should().BeTrue();

        await _award.Received(1).AwardAsync(
            round.SpyUserId, 30, Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        // После разгадки игра завершена — голосовать нельзя
        (_, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        Guid citizenId = participants.First(p => !p.IsSpy).UserId;
        SpyFallOutcome vote = await _service.VoteAsync(sessionId, citizenId, round.SpyUserId);
        vote.Success.Should().BeFalse();
        vote.Message.Should().Contain("уже завершена");
    }

    [Fact]
    public async Task Guess_WrongWord_BurnsSingleAttemptAndGameContinues()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, _) = await LoadRoundAsync(sessionId);

        SpyFallOutcome wrong = await _service.GuessAsync(sessionId, round.SpyUserId, "мимо");

        wrong.Success.Should().BeTrue();
        wrong.Message.Should().Contain("Попытка использована");
        wrong.PointsAwarded.Should().Be(0);

        (SpyFallRound after, _) = await LoadRoundAsync(sessionId);
        after.GuessUsedAt.Should().NotBeNull();
        after.GuessCorrect.Should().BeFalse();
        after.ResolvedAt.Should().BeNull();
        after.Winner.Should().BeNull();

        // Вторая попытка запрещена
        SpyFallOutcome second = await _service.GuessAsync(sessionId, round.SpyUserId, round.CitizenWord);
        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже использована");

        await _award.DidNotReceive().AwardAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Guess_ByCitizen_IsRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        Guid citizenId = participants.First(p => !p.IsSpy).UserId;

        SpyFallOutcome outcome = await _service.GuessAsync(sessionId, citizenId, round.CitizenWord);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("только шпион");
    }

    // === Закрытие без результата и данные ===

    [Fact]
    public async Task CloseUnresolved_NoPointsAndVotesAreRejected()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);
        Guid citizenId = participants.First(p => !p.IsSpy).UserId;

        await _service.CloseUnresolvedAsync(sessionId);

        (SpyFallRound closed, _) = await LoadRoundAsync(sessionId);
        closed.ResolvedBy.Should().Be("closed");
        closed.Winner.Should().BeNull();

        SpyFallOutcome vote = await _service.VoteAsync(sessionId, citizenId, round.SpyUserId);
        vote.Success.Should().BeFalse();

        await _award.DidNotReceive().AwardAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        JsonElement live = Json(await _service.GetLiveStateAsync(sessionId));
        live.GetProperty("resolvedBy").GetString().Should().Be("closed");
        live.GetProperty("winner").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetPlayerState_SpySeesSpyWord_CitizenSeesCitizenWord()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);
        (SpyFallRound round, List<SpyFallParticipant> participants) = await LoadRoundAsync(sessionId);

        JsonElement spy = Json(await _service.GetPlayerStateAsync(sessionId, round.SpyUserId));
        spy.GetProperty("role").GetString().Should().Be("spy");
        spy.GetProperty("word").GetString().Should().Be(round.SpyWord);
        spy.GetProperty("canVoteFor").GetArrayLength().Should().Be(3);
        spy.GetProperty("attemptUsed").GetBoolean().Should().BeFalse();

        Guid citizenId = participants.First(p => !p.IsSpy).UserId;
        JsonElement citizen = Json(await _service.GetPlayerStateAsync(sessionId, citizenId));
        citizen.GetProperty("role").GetString().Should().Be("citizen");
        citizen.GetProperty("word").GetString().Should().Be(round.CitizenWord);
        citizen.GetProperty("citizensCount").GetInt32().Should().Be(3);
        citizen.GetProperty("votedCount").GetInt32().Should().Be(0);
        citizen.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetLiveState_DuringPlay_HidesRolesAndWords()
    {
        (EventDefinition definition, List<User> players) = await SeedEventAsync();
        Guid sessionId = await StartGameAsync(definition, players);

        JsonElement live = Json(await _service.GetLiveStateAsync(sessionId));

        live.GetProperty("phase").GetString().Should().Be("playing");
        live.GetProperty("votedCount").GetInt32().Should().Be(0);
        live.GetProperty("citizensCount").GetInt32().Should().Be(3);
        live.TryGetProperty("spyName", out _).Should().BeFalse();
        live.TryGetProperty("citizenWord", out _).Should().BeFalse();
    }

    // === Конфиг ===

    [Fact]
    public void ParseConfig_InvalidJson_UsesDefaults()
    {
        SpyFallService.SpyFallConfig config = SpyFallService.ParseConfig("не json");

        config.PointsSpy.Should().Be(50);
        config.PointsCitizen.Should().Be(25);
        config.Pairs.Should().BeEmpty();
    }

    [Fact]
    public void ParseConfig_FiltersEmptyPairsAndNegativePoints()
    {
        const string config = """
            {"pointsSpy":-5,"pointsCitizen":7,"pairs":[
                {"citizen":"торт","spy":"пирожное"},
                {"citizen":"","spy":"кофе"},
                {"citizen":"кот","spy":"  "}
            ]}
            """;

        SpyFallService.SpyFallConfig parsed = SpyFallService.ParseConfig(config);

        parsed.PointsSpy.Should().Be(50);
        parsed.PointsCitizen.Should().Be(7);
        parsed.Pairs.Should().ContainSingle().Which.Citizen.Should().Be("торт");
    }

    [Theory]
    [InlineData("Пирожное", "пирожное")]
    [InlineData("  торт  ", "торт")]
    [InlineData("чёрный  кот", "черный кот")]
    public void Normalize_IgnoresCaseSpacesAndYo(string input, string expected)
    {
        SpyFallService.Normalize(input).Should().Be(expected);
    }
}