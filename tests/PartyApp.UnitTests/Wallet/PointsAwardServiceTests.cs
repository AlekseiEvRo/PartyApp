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

    [Fact]
    public async Task TrySpendAsync_WithEnoughPoints_DeductsAndWritesTransaction()
    {
        User user = await SeedUserAsync(walletBalance: 50);

        int? newBalance = await _service.TrySpendAsync(user.Id, 30, "Покупка: торт");

        newBalance.Should().Be(20);
        (await _host.DbAsync(db => db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == user.Id))).Balance
            .Should().Be(20);

        WalletTransaction transaction = await _host.DbAsync(db => db.WalletTransactions.AsNoTracking().SingleAsync());
        transaction.Amount.Should().Be(-30);
        transaction.Type.Should().Be(WalletTransactionType.ShopPurchase);
        transaction.Description.Should().Be("Покупка: торт");

        BalanceFrom(_hub.SingleCall("BalanceUpdated")).Should().Be(20);
    }

    [Fact]
    public async Task TrySpendAsync_WithExactBalance_LeavesZero()
    {
        User user = await SeedUserAsync(walletBalance: 25);

        int? newBalance = await _service.TrySpendAsync(user.Id, 25, "Покупка");

        newBalance.Should().Be(0);
    }

    [Fact]
    public async Task TrySpendAsync_WithoutEnoughPoints_ReturnsNullAndKeepsBalance()
    {
        User user = await SeedUserAsync(walletBalance: 10);

        int? newBalance = await _service.TrySpendAsync(user.Id, 11, "Покупка");

        newBalance.Should().BeNull();
        (await _host.Db.Wallets.SingleAsync(w => w.UserId == user.Id)).Balance.Should().Be(10);
        (await _host.Db.WalletTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TrySpendAsync_WithoutWallet_ReturnsNull()
    {
        User user = await SeedUserAsync();

        int? newBalance = await _service.TrySpendAsync(user.Id, 5, "Покупка");

        newBalance.Should().BeNull();
    }

    [Fact]
    public async Task TrySpendAsync_WithNonPositiveAmount_Throws()
    {
        User user = await SeedUserAsync(walletBalance: 10);

        Func<Task> act = () => _service.TrySpendAsync(user.Id, 0, "Покупка");

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task TransferAsync_MovesPointsAndWritesBothTransactions()
    {
        User sender = await SeedUserAsync("sender", walletBalance: 50);
        User recipient = await SeedUserAsync("recipient", walletBalance: 10);

        TransferOutcome? outcome = await _service.TransferAsync(sender.Id, recipient.Id, 20, "На такси");

        outcome.Should().NotBeNull();
        outcome!.SenderBalance.Should().Be(30);
        outcome.RecipientBalance.Should().Be(30);

        (await _host.Db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == sender.Id)).Balance.Should().Be(30);
        (await _host.Db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == recipient.Id)).Balance.Should().Be(30);

        List<WalletTransaction> transactions = await _host.Db.WalletTransactions.AsNoTracking().ToListAsync();
        transactions.Should().HaveCount(2);

        WalletTransaction outgoing = transactions.Single(t => t.Type == WalletTransactionType.TransferOut);
        outgoing.Amount.Should().Be(-20);
        outgoing.Description.Should().Be("Перевод игроку recipient: На такси");

        WalletTransaction incoming = transactions.Single(t => t.Type == WalletTransactionType.TransferIn);
        incoming.Amount.Should().Be(20);
        incoming.Description.Should().Be("Перевод от sender: На такси");
    }

    [Fact]
    public async Task TransferAsync_NotifiesBothSides()
    {
        User sender = await SeedUserAsync("sender", walletBalance: 50);
        User recipient = await SeedUserAsync("recipient");

        await _service.TransferAsync(sender.Id, recipient.Id, 15);

        List<RecordingHubContext.HubCall> calls = _hub.CallsFor("BalanceUpdated").ToList();
        calls.Should().HaveCount(2);
        calls.Select(c => c.Target)
            .Should().BeEquivalentTo($"user:{sender.Id}", $"user:{recipient.Id}");

        _push.Calls.Should().HaveCount(2);
        _push.Calls.Should().ContainSingle(c =>
            c.UserIds != null && c.UserIds.Contains(sender.Id) && c.Message.Title == "−15 баллов");
        _push.Calls.Should().ContainSingle(c =>
            c.UserIds != null && c.UserIds.Contains(recipient.Id) && c.Message.Title == "⭐ +15 баллов");
    }

    [Fact]
    public async Task TransferAsync_WithoutEnoughPoints_ReturnsNullAndKeepsBalances()
    {
        User sender = await SeedUserAsync("sender", walletBalance: 10);
        User recipient = await SeedUserAsync("recipient", walletBalance: 5);

        TransferOutcome? outcome = await _service.TransferAsync(sender.Id, recipient.Id, 11);

        outcome.Should().BeNull();
        (await _host.Db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == sender.Id)).Balance.Should().Be(10);
        (await _host.Db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == recipient.Id)).Balance.Should().Be(5);
        (await _host.Db.WalletTransactions.CountAsync()).Should().Be(0);
        _hub.CallsFor("BalanceUpdated").Should().BeEmpty();
        _push.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TransferAsync_RecipientWithoutWallet_CreatesIt()
    {
        User sender = await SeedUserAsync("sender", walletBalance: 40);
        User recipient = await SeedUserAsync("recipient");

        TransferOutcome? outcome = await _service.TransferAsync(sender.Id, recipient.Id, 25);

        outcome!.RecipientBalance.Should().Be(25);
        (await _host.Db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == recipient.Id)).Balance.Should().Be(25);
    }

    [Fact]
    public async Task TransferAsync_UnknownSender_ReturnsNull()
    {
        User recipient = await SeedUserAsync("recipient", walletBalance: 5);

        TransferOutcome? outcome = await _service.TransferAsync(Guid.NewGuid(), recipient.Id, 5);

        outcome.Should().BeNull();
    }

    [Fact]
    public async Task TransferAsync_ToSelf_Throws()
    {
        User user = await SeedUserAsync(walletBalance: 10);

        Func<Task> act = () => _service.TransferAsync(user.Id, user.Id, 5);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TransferAsync_WithNonPositiveAmount_Throws()
    {
        User sender = await SeedUserAsync("sender", walletBalance: 10);
        User recipient = await SeedUserAsync("recipient");

        Func<Task> act = () => _service.TransferAsync(sender.Id, recipient.Id, 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}