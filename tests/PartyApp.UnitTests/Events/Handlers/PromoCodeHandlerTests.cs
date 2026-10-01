using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

public class PromoCodeHandlerTests : IDisposable
{
    private const string ConfigJson = """
        {
            "codes": ["СДР2025", "АЛЕКСЕЙ"],
            "pointsPerCode": 15,
            "oneTimePerPlayer": true
        }
        """;

    private readonly SqliteTestHost _host;
    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly PromoCodeHandler _handler;

    public PromoCodeHandlerTests()
    {
        _host = new SqliteTestHost();
        _handler = new PromoCodeHandler(_host.ScopeFactory, _award, NullLogger<PromoCodeHandler>.Instance);
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
        EventDefinition definition = TestData.Definition("promo_code", configJson);
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

    [Fact]
    public async Task HandleSubmission_WithoutConfiguredCodes_Fails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync("{}");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"СДР2025"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Промокоды не настроены");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"code":""}""")]
    [InlineData("""{"code":"   "}""")]
    [InlineData("""{"code":123}""")]
    [InlineData("not-json")]
    public async Task HandleSubmission_WithoutCode_Fails(string payload)
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(session, definition, player.Id, payload);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Введи промокод");
    }

    [Fact]
    public async Task HandleSubmission_WithUnknownCode_Fails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"НЕВЕРНЫЙ"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Неверный промокод");
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task HandleSubmission_WithValidCode_AwardsPoints()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"СДР2025"}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(15);
        result.Message.Should().Be("Промокод «СДР2025» активирован!");

        await _award.Received(1).AwardAsync(
            player.Id,
            15,
            "Промокод: СДР2025",
            WalletTransactionType.EventReward,
            session.Id,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("сдр2025")]
    [InlineData("СдР2025")]
    [InlineData("  СДР2025  ")]
    public async Task HandleSubmission_CodeMatching_IgnoresCaseAndSpaces(string submitted)
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, $$"""{"code":"{{submitted}}"}""");

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Промокод «СДР2025» активирован!");
        await _award.Received(1).AwardAsync(
            player.Id, 15, "Промокод: СДР2025", Arg.Any<WalletTransactionType>(), session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSubmission_UsingSameCodeTwice_Fails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: """{"code":"сдр2025"}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"СДР2025"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Ты уже активировал этот код");
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task HandleSubmission_AfterOneCode_AnotherCodeStillWorks()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: """{"code":"СДР2025"}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"АЛЕКСЕЙ"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_CodeUsedByOtherPlayer_IsAllowed()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        User otherPlayer = TestData.User("other");
        _host.Db.Users.Add(otherPlayer);
        _host.Db.PlayerSubmissions.Add(new PlayerSubmission
        {
            SessionId = session.Id,
            PlayerId = otherPlayer.Id,
            PayloadJson = """{"code":"СДР2025"}"""
        });
        await _host.Db.SaveChangesAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"СДР2025"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_RepeatedCode_IsAllowedWhenOneTimeDisabled()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            """
            { "codes": ["СДР2025"], "pointsPerCode": 15, "oneTimePerPlayer": false }
            """,
            playerSubmissionPayloads: """{"code":"СДР2025"}""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"СДР2025"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_WithoutPointsPerCode_FallsBackToFifteen()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            """{ "codes": ["КОД"] }""");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"КОД"}""");

        result.PointsAwarded.Should().Be(15);
    }

    [Fact]
    public async Task HandleSubmission_WithBrokenStoredPayload_IsIgnored()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            playerSubmissionPayloads: "not-json");

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"СДР2025"}""");

        result.Success.Should().BeTrue();
    }
}