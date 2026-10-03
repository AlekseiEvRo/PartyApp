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

public class QrScanHandlerTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly RecordingHubContext _hub = new();
    private readonly QrScanHandler _handler;

    public QrScanHandlerTests()
    {
        _host = new SqliteTestHost();
        _handler = new QrScanHandler(_host.ScopeFactory, _award, _hub, NullLogger<QrScanHandler>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private async Task<(User Player, EventDefinition Definition, EventSession Session)> SeedAsync(
        params QrToken[] tokens)
    {
        User admin = TestData.User("admin", UserRole.Admin);
        User player = TestData.User("player");
        EventDefinition definition = TestData.Definition("qr_scan", "{}");
        EventSession session = TestData.Session(definition, admin.Id);

        _host.Db.Users.AddRange(admin, player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        _host.Db.QrTokens.AddRange(tokens);
        await _host.Db.SaveChangesAsync();

        return (player, definition, session);
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
        result.Message.Should().Be("Введи код с QR");
    }

    [Fact]
    public async Task HandleSubmission_WithUnknownCode_Fails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"NOPE42"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Код не найден");
    }

    [Fact]
    public async Task HandleSubmission_WithRedeemedToken_Fails()
    {
        QrToken redeemed = TestData.QrToken("USED42", points: 30);
        redeemed.RedeemedAt = DateTime.UtcNow;
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(redeemed);

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"USED42"}""");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Этот код уже был использован");
        (await _host.Db.Wallets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task HandleSubmission_WithValidToken_CreatesWalletAndMarksTokenRedeemed()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            TestData.QrToken("ABC234", points: 30));

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"ABC234"}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(30);
        result.Message.Should().Be("QR-код активирован! +30 баллов");

        Wallet wallet = await _host.Db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == player.Id);
        wallet.Balance.Should().Be(30);

        WalletTransaction transaction = await _host.Db.WalletTransactions.SingleAsync();
        transaction.Amount.Should().Be(30);
        transaction.Type.Should().Be(WalletTransactionType.QrBonus);
        transaction.Description.Should().Be("QR-код: ABC234");
        transaction.RelatedSessionId.Should().Be(session.Id);

        QrToken token = await _host.Db.QrTokens.AsNoTracking().SingleAsync(t => t.Code == "ABC234");
        token.RedeemedById.Should().Be(player.Id);
        token.RedeemedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleSubmission_WithExistingWallet_IncreasesBalance()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(TestData.QrToken("ABC234", points: 20));

        _host.Db.Wallets.Add(new Wallet { UserId = player.Id, Balance = 100 });
        await _host.Db.SaveChangesAsync();

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"ABC234"}""");

        result.Success.Should().BeTrue();
        Wallet wallet = await _host.Db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == player.Id);
        wallet.Balance.Should().Be(120);
    }

    [Fact]
    public async Task HandleSubmission_LowercaseCode_IsNormalizedToUppercase()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            TestData.QrToken("ABC234", points: 20));

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"abc234"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSubmission_WithoutTokenPoints_FallsBackToTwenty()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            TestData.QrToken("ZERO00", points: 0));

        SubmissionResult result = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"ZERO00"}""");

        result.Success.Should().BeTrue();
        result.PointsAwarded.Should().Be(20);
    }

    [Fact]
    public async Task HandleSubmission_NotifiesBalanceChanged()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            TestData.QrToken("ABC234", points: 25));

        await _handler.HandleSubmissionAsync(session, definition, player.Id, """{"code":"ABC234"}""");

        await _award.Received(1).NotifyBalanceChangedAsync(
            player.Id,
            25,
            25,
            "QR-код: ABC234",
            Arg.Any<CancellationToken>());
        await _award.DidNotReceiveWithAnyArgs().AwardAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task HandleSubmission_BroadcastsQrRedeemed()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            TestData.QrToken("ABC234", points: 25));

        await _handler.HandleSubmissionAsync(session, definition, player.Id, """{"code":"ABC234"}""");

        RecordingHubContext.HubCall call = _hub.SingleCall("QrRedeemed");
        call.Target.Should().Be("all");
    }

    [Fact]
    public async Task HandleSubmission_SameTokenTwice_SecondAttemptFails()
    {
        (User player, EventDefinition definition, EventSession session) = await SeedAsync(
            TestData.QrToken("ABC234", points: 20));

        SubmissionResult first = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"ABC234"}""");
        SubmissionResult second = await _handler.HandleSubmissionAsync(
            session, definition, player.Id, """{"code":"ABC234"}""");

        first.Success.Should().BeTrue();
        second.Success.Should().BeFalse();
        second.Message.Should().Be("Этот код уже был использован");
        (await _host.Db.Wallets.AsNoTracking().SingleAsync()).Balance.Should().Be(20);
    }
}