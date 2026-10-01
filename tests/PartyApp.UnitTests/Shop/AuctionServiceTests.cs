using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using PartyApp.Api.Modules.Shop;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Shop;

public class AuctionServiceTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly RecordingHubContext _hub = new();
    private readonly FakePushNotificationService _push = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly PointsAwardService _pointsAward;
    private readonly AuctionService _service;

    public AuctionServiceTests()
    {
        _host = new SqliteTestHost();
        _pointsAward = new PointsAwardService(
            _host.ScopeFactory,
            _hub,
            _push,
            NullLogger<PointsAwardService>.Instance);

        _service = new AuctionService(
            _host.ScopeFactory,
            _pointsAward,
            _hub,
            _push,
            _timeProvider,
            NullLogger<AuctionService>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private async Task<User> SeedUserAsync(int balance)
    {
        User user = TestData.User($"user-{Guid.NewGuid():N}"[..12]);
        _host.Db.Users.Add(user);
        _host.Db.Wallets.Add(new Wallet { UserId = user.Id, Balance = balance });
        await _host.Db.SaveChangesAsync();
        return user;
    }

    private async Task<Lot> SeedLotAsync(int minBid = 10, int endsInMinutes = 30)
    {
        var lot = new Lot
        {
            Name = "Торт от именинника",
            MinBid = minBid,
            EndsAt = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(endsInMinutes)
        };

        _host.Db.Lots.Add(lot);
        await _host.Db.SaveChangesAsync();
        return lot;
    }

    private async Task<int> BalanceOfAsync(User user)
    {
        return await _host.DbAsync(db => db.Wallets.AsNoTracking()
            .Where(w => w.UserId == user.Id)
            .Select(w => w.Balance)
            .SingleAsync());
    }

    /// <summary>Читает данные в свежем scope: долгоживущий контекст видит устаревшие сущности.</summary>
    private Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        return _host.DbAsync(action);
    }

    [Fact]
    public async Task PlaceBid_FirstBid_HoldsPoints()
    {
        User player = await SeedUserAsync(balance: 100);
        Lot lot = await SeedLotAsync();

        BidOutcome outcome = await _service.PlaceBidAsync(lot.Id, player.Id, 40);

        outcome.Success.Should().BeTrue();
        outcome.NewBalance.Should().Be(60);
        (await BalanceOfAsync(player)).Should().Be(60);

        Bid bid = await ReadAsync(db => db.Bids.AsNoTracking().SingleAsync(b => b.LotId == lot.Id));
        bid.Amount.Should().Be(40);
    }

    [Fact]
    public async Task PlaceBid_Raise_ChargesOnlyDifference()
    {
        User player = await SeedUserAsync(balance: 100);
        Lot lot = await SeedLotAsync();

        await _service.PlaceBidAsync(lot.Id, player.Id, 40);
        BidOutcome outcome = await _service.PlaceBidAsync(lot.Id, player.Id, 70);

        outcome.Success.Should().BeTrue();
        outcome.NewBalance.Should().Be(30);
        (await ReadAsync(db => db.Bids.AsNoTracking().SingleAsync(b => b.LotId == lot.Id))).Amount.Should().Be(70);
    }

    [Fact]
    public async Task PlaceBid_NotHigherThanOwn_IsRejected()
    {
        User player = await SeedUserAsync(balance: 100);
        Lot lot = await SeedLotAsync();

        await _service.PlaceBidAsync(lot.Id, player.Id, 50);
        BidOutcome outcome = await _service.PlaceBidAsync(lot.Id, player.Id, 50);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("больше вашей текущей");
        (await BalanceOfAsync(player)).Should().Be(50);
    }

    [Fact]
    public async Task PlaceBid_AfterDeadline_IsRejected()
    {
        User player = await SeedUserAsync(balance: 100);
        Lot lot = await SeedLotAsync(endsInMinutes: 5);

        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        BidOutcome outcome = await _service.PlaceBidAsync(lot.Id, player.Id, 40);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("закончился");
        (await ReadAsync(db => db.Bids.AsNoTracking().CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task PlaceBid_WithoutEnoughPoints_IsRejectedAndKeepsBalance()
    {
        User player = await SeedUserAsync(balance: 10);
        Lot lot = await SeedLotAsync(minBid: 50);

        BidOutcome outcome = await _service.PlaceBidAsync(lot.Id, player.Id, 50);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Be("Недостаточно баллов");
        (await BalanceOfAsync(player)).Should().Be(10);
        (await ReadAsync(db => db.Bids.AsNoTracking().CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task CloseLot_HighestBidWins_AndLosersGetRefund()
    {
        User alice = await SeedUserAsync(balance: 100);
        User boris = await SeedUserAsync(balance: 100);
        Lot lot = await SeedLotAsync();

        await _service.PlaceBidAsync(lot.Id, alice.Id, 40);
        await _service.PlaceBidAsync(lot.Id, boris.Id, 70);

        LotCloseOutcome outcome = await _service.CloseLotAsync(lot.Id);

        outcome.Success.Should().BeTrue();
        (await BalanceOfAsync(alice)).Should().Be(100); // возврат
        (await BalanceOfAsync(boris)).Should().Be(30);  // ставка осталась у лота

        Lot stored = await ReadAsync(db => db.Lots.AsNoTracking().SingleAsync(l => l.Id == lot.Id));
        stored.Status.Should().Be(LotStatus.Finished);
        stored.WinnerId.Should().Be(boris.Id);
        stored.WinningBid.Should().Be(70);

        _hub.SingleCall("LotFinished").Should().NotBeNull();
        _push.Calls.Should().Contain(call => call.Message.Tag == $"lot-{lot.Id}");
    }

    [Fact]
    public async Task CloseLot_WhenAlreadyClosed_Fails()
    {
        User player = await SeedUserAsync(balance: 100);
        Lot lot = await SeedLotAsync();

        await _service.PlaceBidAsync(lot.Id, player.Id, 40);
        await _service.CloseLotAsync(lot.Id);

        LotCloseOutcome second = await _service.CloseLotAsync(lot.Id);

        second.Success.Should().BeFalse();
        second.Message.Should().Contain("уже закрыт");
    }

    [Fact]
    public async Task CloseLot_WithoutBids_FinishesWithoutWinner()
    {
        Lot lot = await SeedLotAsync();

        LotCloseOutcome outcome = await _service.CloseLotAsync(lot.Id);

        outcome.Success.Should().BeTrue();
        Lot stored = await ReadAsync(db => db.Lots.AsNoTracking().SingleAsync(l => l.Id == lot.Id));
        stored.Status.Should().Be(LotStatus.Finished);
        stored.WinnerId.Should().BeNull();
    }

    [Fact]
    public async Task CancelLot_RefundsEveryBid()
    {
        User alice = await SeedUserAsync(balance: 100);
        User boris = await SeedUserAsync(balance: 100);
        Lot lot = await SeedLotAsync();

        await _service.PlaceBidAsync(lot.Id, alice.Id, 30);
        await _service.PlaceBidAsync(lot.Id, boris.Id, 60);

        LotCloseOutcome outcome = await _service.CancelLotAsync(lot.Id);

        outcome.Success.Should().BeTrue();
        (await BalanceOfAsync(alice)).Should().Be(100);
        (await BalanceOfAsync(boris)).Should().Be(100);
        (await ReadAsync(db => db.Lots.AsNoTracking().SingleAsync(l => l.Id == lot.Id))).Status
            .Should().Be(LotStatus.Cancelled);
        _hub.SingleCall("LotCancelled").Should().NotBeNull();
    }

    [Fact]
    public async Task CloseExpired_ClosesOnlyExpiredLots()
    {
        User player = await SeedUserAsync(balance: 100);
        Lot expired = await SeedLotAsync(endsInMinutes: 5);
        Lot active = await SeedLotAsync(endsInMinutes: 60);

        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        await _service.CloseExpiredAsync();

        (await ReadAsync(db => db.Lots.AsNoTracking().SingleAsync(l => l.Id == expired.Id))).Status
            .Should().Be(LotStatus.Finished);
        (await ReadAsync(db => db.Lots.AsNoTracking().SingleAsync(l => l.Id == active.Id))).Status
            .Should().Be(LotStatus.Open);
    }
}