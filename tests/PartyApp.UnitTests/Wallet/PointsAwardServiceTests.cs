using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.WalletTests;

public class PointsAwardServiceTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly RecordingHubContext _hub = new();
    private readonly FakePushNotificationService _push = new();
    private readonly PointsAwardService _service;

    public PointsAwardServiceTests()
    {
        _host = new SqliteTestHost();
        _service = new PointsAwardService(
            _host.ScopeFactory,
            _hub,
            _push,
            NullLogger<PointsAwardService>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private async Task<User> SeedUserAsync(string username = "alice", int? walletBalance = null)
    {
        User user = TestData.User(username);
        _host.Db.Users.Add(user);

        if (walletBalance.HasValue)
            _host.Db.Wallets.Add(new Wallet { UserId = user.Id, Balance = walletBalance.Value });

        await _host.Db.SaveChangesAsync();
        return user;
    }

    private sealed record BalancePayload(int Balance);

    private static readonly JsonSerializerOptions PayloadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static int BalanceFrom(RecordingHubContext.HubCall call)
    {
        return JsonSerializer.Deserialize<BalancePayload>(JsonSerializer.Serialize(call.Payload), PayloadJsonOptions)!.Balance;
    }

    [Fact]
    public async Task AwardAsync_WithoutWallet_CreatesWalletWithAmount()
    {
        User user = await SeedUserAsync();

        int newBalance = await _service.AwardAsync(user.Id, 40, "Подарок");

        newBalance.Should().Be(40);
        Wallet wallet = await _host.Db.Wallets.SingleAsync(w => w.UserId == user.Id);
        wallet.Balance.Should().Be(40);
    }

    [Fact]
    public async Task AwardAsync_WithExistingWallet_IncreasesBalance()
    {
        User user = await SeedUserAsync(walletBalance: 50);

        int newBalance = await _service.AwardAsync(user.Id, 25, "Квиз");

        newBalance.Should().Be(75);
        (await _host.Db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == user.Id)).Balance.Should().Be(75);
    }

    [Fact]
    public async Task AwardAsync_NegativeAmount_DecreasesBalance()
    {
        User user = await SeedUserAsync(walletBalance: 50);

        int newBalance = await _service.AwardAsync(user.Id, -30, "Штраф", WalletTransactionType.AdminDeduct);

        newBalance.Should().Be(20);
    }

    [Fact]
    public async Task AwardAsync_WritesTransactionWithTypeDescriptionAndSession()
    {
        User user = await SeedUserAsync();
        EventDefinition definition = TestData.Definition("quiz");
        EventSession session = TestData.Session(definition, user.Id);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        await _service.AwardAsync(user.Id, 15, "Промокод: СДР2025", WalletTransactionType.EventReward, session.Id);

        WalletTransaction transaction = await _host.Db.WalletTransactions.SingleAsync();
        transaction.Amount.Should().Be(15);
        transaction.Type.Should().Be(WalletTransactionType.EventReward);
        transaction.Description.Should().Be("Промокод: СДР2025");
        transaction.RelatedSessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task AwardAsync_SendsBalanceUpdatedToUserViaSignalR()
    {
        User user = await SeedUserAsync();

        await _service.AwardAsync(user.Id, 40, "Подарок");

        RecordingHubContext.HubCall call = _hub.SingleCall("BalanceUpdated");
        call.Target.Should().Be($"user:{user.Id}");
        BalanceFrom(call).Should().Be(40);
    }

    [Fact]
    public async Task AwardAsync_SendsPushWithAmountDescriptionAndTag()
    {
        User user = await SeedUserAsync();

        await _service.AwardAsync(user.Id, 25, "Правильный ответ");

        PushCall call = _push.Calls.Should().ContainSingle().Subject;
        call.UserIds.Should().Equal(user.Id);
        call.Message.Title.Should().Be("⭐ +25 баллов");
        call.Message.Body.Should().Be("Правильный ответ");
        call.Message.Tag.Should().Be($"balance-{user.Id}");
        call.Message.ThrottleWindow.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task AwardAsync_NegativeAmount_PushTitleShowsMinus()
    {
        User user = await SeedUserAsync(walletBalance: 100);

        await _service.AwardAsync(user.Id, -30, "Ручное списание", WalletTransactionType.AdminDeduct);

        PushCall call = _push.Calls.Should().ContainSingle().Subject;
        call.Message.Title.Should().Be("−30 баллов");
    }

    [Fact]
    public async Task AwardAsync_ZeroAmount_SendsBalanceButNoPush()
    {
        User user = await SeedUserAsync(walletBalance: 10);

        int newBalance = await _service.AwardAsync(user.Id, 0, "Без изменений");

        newBalance.Should().Be(10);
        _hub.SingleCall("BalanceUpdated").Should().NotBeNull();
        _push.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AwardAsync_TwoAwards_NotifiesLatestBalanceEveryTime()
    {
        User user = await SeedUserAsync();

        await _service.AwardAsync(user.Id, 10, "Первый");
        await _service.AwardAsync(user.Id, 5, "Второй");

        List<int> notified = _hub.CallsFor("BalanceUpdated").Select(BalanceFrom).ToList();
        notified.Should().Equal(10, 15);
    }

    [Fact]
    public async Task NotifyBalanceChangedAsync_SendsSignalRAndPushWithoutTouchingDatabase()
    {
        User user = await SeedUserAsync();

        await _service.NotifyBalanceChangedAsync(user.Id, 77, 7, "QR-код: ABC234");

        BalanceFrom(_hub.SingleCall("BalanceUpdated")).Should().Be(77);
        _push.Calls.Should().ContainSingle();
        (await _host.Db.WalletTransactions.CountAsync()).Should().Be(0);
        (await _host.Db.Wallets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task NotifyBalanceChangedAsync_WithZeroAmount_SkipsPush()
    {
        User user = await SeedUserAsync();

        await _service.NotifyBalanceChangedAsync(user.Id, 42, 0, "Баланс пересчитан");

        _hub.SingleCall("BalanceUpdated").Should().NotBeNull();
        _push.Calls.Should().BeEmpty();
    }
}