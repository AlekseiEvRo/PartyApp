using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.SpyGame;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

using SubmissionResult = PartyApp.Api.Modules.SpyGame.SpyGameService.SubmissionResult;

namespace PartyApp.UnitTests.SpyGame;

public class SpyGameServiceTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly RecordingHubContext _hub = new();
    private readonly FakePushNotificationService _push = new();
    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly SpyGameService _service;

    public SpyGameServiceTests()
    {
        _host = new SqliteTestHost();
        _service = new SpyGameService(
            _host.ScopeFactory,
            _hub,
            _push,
            _award,
            NullLogger<SpyGameService>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private int _playerCounter;

    private async Task<List<User>> SeedPlayersAsync(int count)
    {
        List<User> users = Enumerable.Range(0, count)
            .Select(_ =>
            {
                int index = _playerCounter++;
                return TestData.User($"player{index}", displayName: $"Игрок {index}");
            })
            .ToList();

        _host.Db.Users.AddRange(users);
        await _host.Db.SaveChangesAsync();
        return users;
    }

    private static JsonElement Json(object? value)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;
    }

    // === StartGameAsync ===

    [Fact]
    public async Task StartGame_WithThreePlayers_Throws()
    {
        List<User> players = await SeedPlayersAsync(3);

        Func<Task> act = () => _service.StartGameAsync(players.Select(p => p.Id).ToList());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Нужно минимум 4 игрока");
    }

    [Fact]
    public async Task StartGame_WithDuplicatePlayers_Throws()
    {
        List<User> players = await SeedPlayersAsync(4);
        List<Guid> ids = players.Select(p => p.Id).ToList();
        ids.Add(ids[0]);

        Func<Task> act = () => _service.StartGameAsync(ids);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Игроки не должны повторяться");
    }

    [Fact]
    public async Task StartGame_WhenSomePlayersNotFound_Throws()
    {
        List<User> players = await SeedPlayersAsync(4);
        List<Guid> ids = players.Select(p => p.Id).ToList();
        ids[0] = Guid.NewGuid();

        Func<Task> act = () => _service.StartGameAsync(ids);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Не все игроки найдены");
    }

    [Fact]
    public async Task StartGame_WhileGameIsPlaying_Throws()
    {
        List<User> players = await SeedPlayersAsync(4);
        await _service.StartGameAsync(players.Select(p => p.Id).ToList());

        Func<Task> act = () => _service.StartGameAsync(players.Select(p => p.Id).ToList());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Игра уже запущена");
    }

    [Fact]
    public async Task StartGame_AssignsExactlyTwoSpiesWithNarratorAndGuesser()
    {
        List<User> players = await SeedPlayersAsync(6);

        SpyGameState state = await _service.StartGameAsync(players.Select(p => p.Id).ToList());

        state.Phase.Should().Be(SpyGamePhase.Playing);
        state.SessionId.Should().NotBe(Guid.Empty);
        state.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        state.SecretWord.Should().NotBeNullOrWhiteSpace();
        state.Players.Should().HaveCount(6);

        List<SpyPlayerRole> spies = state.Spies.ToList();
        spies.Should().HaveCount(2);
        state.Townsfolk.Should().HaveCount(4);

        spies.Should().ContainSingle(s => s.IsGuesser);
        SpyPlayerRole narrator = spies.Single(s => !s.IsGuesser);
        SpyPlayerRole guesser = spies.Single(s => s.IsGuesser);

        narrator.SecretWord.Should().Be(state.SecretWord);
        guesser.SecretWord.Should().BeNull();

        narrator.PartnerId.Should().Be(guesser.UserId);
        narrator.PartnerName.Should().Be(guesser.DisplayName);
        guesser.PartnerId.Should().Be(narrator.UserId);
        guesser.PartnerName.Should().Be(narrator.DisplayName);
    }

    [Fact]
    public async Task StartGame_WithFourPlayers_AssignsTwoSpiesAndTwoTownsfolk()
    {
        List<User> players = await SeedPlayersAsync(4);

        SpyGameState state = await _service.StartGameAsync(players.Select(p => p.Id).ToList());

        state.Spies.Should().HaveCount(2);
        state.Townsfolk.Should().HaveCount(2);
        state.Players.Should().OnlyContain(p => p.HasAccused == false);
    }

    [Fact]
    public async Task StartGame_SendsRoleAssignedToEachPlayerWithOtherPlayersList()
    {
        List<User> players = await SeedPlayersAsync(4);
        Dictionary<Guid, string> names = players.ToDictionary(p => p.Id, p => p.DisplayName);

        SpyGameState state = await _service.StartGameAsync(players.Select(p => p.Id).ToList());

        List<RecordingHubContext.HubCall> calls = _hub.CallsFor("SpyGameRoleAssigned").ToList();
        calls.Should().HaveCount(4);

        foreach (User player in players)
        {
            RecordingHubContext.HubCall call = calls.Single(c => c.Target == $"user:{player.Id}");
            JsonElement payload = Json(call.Payload);
            SpyPlayerRole role = state.Players.Single(p => p.UserId == player.Id);

            payload.GetProperty("role").GetString().Should().Be(role.Role.ToString());
            payload.GetProperty("isGuesser").GetBoolean().Should().Be(role.IsGuesser);
            payload.GetProperty("sessionId").GetGuid().Should().Be(state.SessionId);

            string? secretWord = payload.GetProperty("secretWord").ValueKind == JsonValueKind.Null
                ? null
                : payload.GetProperty("secretWord").GetString();
            secretWord.Should().Be(role.SecretWord);

            List<Guid> otherPlayers = payload.GetProperty("allPlayers").EnumerateArray()
                .Select(p => p.GetProperty("userId").GetGuid())
                .ToList();
            otherPlayers.Should().HaveCount(3);
            otherPlayers.Should().NotContain(player.Id);
            otherPlayers.Should().BeSubsetOf(names.Keys);
        }
    }

    [Fact]
    public async Task StartGame_BroadcastsStartedAndPushesToParticipants()
    {
        List<User> players = await SeedPlayersAsync(5);

        await _service.StartGameAsync(players.Select(p => p.Id).ToList());

        RecordingHubContext.HubCall call = _hub.SingleCall("SpyGameStarted");
        call.Target.Should().Be("all");
        JsonElement payload = Json(call.Payload);
        payload.GetProperty("playersCount").GetInt32().Should().Be(5);
        payload.GetProperty("spyCount").GetInt32().Should().Be(2);

        PushCall push = _push.Calls.Should().ContainSingle().Subject;
        push.Message.Title.Should().Be("🕵 Игра «Шпионаж» началась!");
        push.Message.Tag.Should().Be("spy-game");
        push.UserIds.Should().BeEquivalentTo(players.Select(p => p.Id));
    }

    // === SubmitWordAsync ===

    private async Task<(SpyGameState State, SpyPlayerRole Narrator, SpyPlayerRole Guesser, SpyPlayerRole Town)>
        StartSeededGameAsync(int playerCount = 4)
    {
        List<User> players = await SeedPlayersAsync(playerCount);
        SpyGameState state = await _service.StartGameAsync(players.Select(p => p.Id).ToList());

        return (
            state,
            state.Spies.Single(s => !s.IsGuesser),
            state.Spies.Single(s => s.IsGuesser),
            state.Townsfolk.First());
    }

    [Fact]
    public async Task SubmitWord_WhenGameNotStarted_Fails()
    {
        SubmissionResult result = await _service.SubmitWordAsync(Guid.NewGuid(), "торт");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Игра не запущена");
    }

    [Fact]
    public async Task SubmitWord_FromNonParticipant_Fails()
    {
        await StartSeededGameAsync();

        SubmissionResult result = await _service.SubmitWordAsync(Guid.NewGuid(), "торт");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Ты не участвуешь в игре");
    }

    [Fact]
    public async Task SubmitWord_FromTownsfolk_Fails()
    {
        (_, _, _, SpyPlayerRole town) = await StartSeededGameAsync();

        SubmissionResult result = await _service.SubmitWordAsync(town.UserId, "торт");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Только шпионы могут вводить слово");
    }

    [Fact]
    public async Task SubmitWord_FromNarrator_Fails()
    {
        (_, SpyPlayerRole narrator, _, _) = await StartSeededGameAsync();

        SubmissionResult result = await _service.SubmitWordAsync(narrator.UserId, "торт");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Ты рассказчик");
    }

    [Fact]
    public async Task SubmitWord_WrongWord_FailsAndGameContinues()
    {
        (SpyGameState state, _, SpyPlayerRole guesser, _) = await StartSeededGameAsync();

        SubmissionResult result = await _service.SubmitWordAsync(guesser.UserId, "заведомо-неверное-слово");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Неверное слово. Попробуй ещё раз.");
        state.Phase.Should().Be(SpyGamePhase.Playing);
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubmitWord_CorrectWord_SpiesWinAndBothGetPoints(bool upperCase)
    {
        (SpyGameState state, SpyPlayerRole narrator, SpyPlayerRole guesser, _) = await StartSeededGameAsync();
        string submitted = upperCase ? state.SecretWord.ToUpperInvariant() : $"  {state.SecretWord}  ";

        SubmissionResult result = await _service.SubmitWordAsync(guesser.UserId, submitted);

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(50);
        result.Message.Should().Be("Шпионы победили!");
        state.Phase.Should().Be(SpyGamePhase.Finished);
        state.Winner.Should().Be("spies");
        state.FinishedAt.Should().NotBeNull();

        foreach (SpyPlayerRole spy in new[] { narrator, guesser })
        {
            await _award.Received(1).AwardAsync(
                spy.UserId,
                50,
                $"Шпионаж: победа (слово: {state.SecretWord})",
                Arg.Any<WalletTransactionType>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
        }

        await _award.DidNotReceive().AwardAsync(
            Arg.Is<Guid>(id => id != narrator.UserId && id != guesser.UserId),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<WalletTransactionType>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        SpyGameResult spyResult = (SpyGameResult)result.Data!;
        spyResult.Winner.Should().Be("spies");
        spyResult.SpyNames.Should().BeEquivalentTo(narrator.DisplayName, guesser.DisplayName);
        spyResult.SecretWord.Should().Be(state.SecretWord);

        RecordingHubContext.HubCall call = _hub.CallsFor("SpyGameFinished").Should().ContainSingle().Subject;
        call.Target.Should().Be("all");
    }

    // === AccuseAsync ===

    [Fact]
    public async Task Accuse_WhenGameNotStarted_Fails()
    {
        SubmissionResult result = await _service.AccuseAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Игра не запущена");
    }

    [Fact]
    public async Task Accuse_FromNonParticipant_Fails()
    {
        await StartSeededGameAsync();

        SubmissionResult result = await _service.AccuseAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Ты не участвуешь в игре");
    }

    [Fact]
    public async Task Accuse_FromSpy_Fails()
    {
        (_, SpyPlayerRole narrator, _, _) = await StartSeededGameAsync();

        SubmissionResult result = await _service.AccuseAsync(narrator.UserId, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Только горожане могут обвинять");
    }

    [Fact]
    public async Task Accuse_Self_Fails()
    {
        (SpyGameState state, _, _, SpyPlayerRole town) = await StartSeededGameAsync();

        SubmissionResult result = await _service.AccuseAsync(town.UserId, town.UserId);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Нельзя обвинять себя");
        state.Players.Single(p => p.UserId == town.UserId).HasAccused.Should().BeFalse();
    }

    [Fact]
    public async Task Accuse_UnknownPlayer_Fails()
    {
        (_, _, _, SpyPlayerRole town) = await StartSeededGameAsync();

        SubmissionResult result = await _service.AccuseAsync(town.UserId, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Игрок не найден");
    }

    [Fact]
    public async Task Accuse_TownsfolkAccusesTownsfolk_SpendsSingleAttempt()
    {
        (SpyGameState state, _, _, SpyPlayerRole town) = await StartSeededGameAsync();
        SpyPlayerRole otherTown = state.Townsfolk.First(p => p.UserId != town.UserId);

        SubmissionResult first = await _service.AccuseAsync(town.UserId, otherTown.UserId);
        SubmissionResult second = await _service.AccuseAsync(town.UserId, otherTown.UserId);

        first.Success.Should().BeFalse();
        first.Message.Should().Be($"{otherTown.DisplayName} — не шпион. У тебя больше нет попыток.");
        second.Success.Should().BeFalse();
        second.Message.Should().Be("Ты уже делал обвинение");
        state.Phase.Should().Be(SpyGamePhase.Playing);
    }

    [Fact]
    public async Task Accuse_CorrectSpy_TownWinsAndAccuserGetsPoints()
    {
        (SpyGameState state, _, SpyPlayerRole guesser, SpyPlayerRole town) = await StartSeededGameAsync();

        SubmissionResult result = await _service.AccuseAsync(town.UserId, guesser.UserId);

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(50);
        result.Message.Should().Be($"{town.DisplayName} разоблачил шпиона!");
        state.Phase.Should().Be(SpyGamePhase.Finished);
        state.Winner.Should().Be("town");
        state.TownWinnerId.Should().Be(town.UserId);
        state.TownWinnerName.Should().Be(town.DisplayName);

        await _award.Received(1).AwardAsync(
            town.UserId,
            50,
            $"Шпионаж: разоблачил шпиона (слово: {state.SecretWord})",
            Arg.Any<WalletTransactionType>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());

        SpyGameResult spyResult = (SpyGameResult)result.Data!;
        spyResult.Winner.Should().Be("town");
        spyResult.TownWinnerName.Should().Be(town.DisplayName);
    }

    // === FinishGameAsync ===

    [Fact]
    public async Task FinishGame_WhenNotPlaying_ReturnsNull()
    {
        (await _service.FinishGameAsync()).Should().BeNull();
    }

    [Fact]
    public async Task FinishGame_DrawWithoutPoints()
    {
        (SpyGameState state, _, _, _) = await StartSeededGameAsync();

        SpyGameResult? result = await _service.FinishGameAsync();

        result.Should().NotBeNull();
        result!.Winner.Should().Be("draw");
        result.PointsAwarded.Should().Be(0);
        result.SecretWord.Should().Be(state.SecretWord);
        state.Phase.Should().Be(SpyGamePhase.Finished);

        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
        _hub.CallsFor("SpyGameFinished").Should().ContainSingle();
    }

    [Fact]
    public async Task StartGame_AfterFinishedGame_StartsNewSession()
    {
        (SpyGameState first, _, _, _) = await StartSeededGameAsync();
        await _service.FinishGameAsync();

        List<User> players = await SeedPlayersAsync(4);
        SpyGameState second = await _service.StartGameAsync(players.Select(p => p.Id).ToList());

        second.SessionId.Should().NotBe(first.SessionId);
        second.Phase.Should().Be(SpyGamePhase.Playing);
        _service.GetCurrentState().SessionId.Should().Be(second.SessionId);
    }
}