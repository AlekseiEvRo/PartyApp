using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Parties;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Parties;

public class PartyServiceTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly RecordingHubContext _hub = new();
    private readonly FakePushNotificationService _push = new();
    private readonly IEventService _events = Substitute.For<IEventService>();
    private readonly IPointsAwardService _pointsAward = Substitute.For<IPointsAwardService>();

    public PartyServiceTests()
    {
        _host = new SqliteTestHost();
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private PartyService CreateService()
    {
        return new PartyService(
            _host.Db,
            _events,
            _pointsAward,
            _hub,
            _push,
            NullLogger<PartyService>.Instance);
    }

    [Fact]
    public async Task StartAsync_CreatesActivePartyAndBroadcasts()
    {
        Party party = await CreateService().StartAsync("Новый год", resetBalances: false);

        party.Status.Should().Be(PartyStatus.Active);
        party.Name.Should().Be("Новый год");

        Party stored = await _host.DbAsync(db => db.Parties.SingleAsync());
        stored.Id.Should().Be(party.Id);

        _hub.SingleCall("PartyStarted").Target.Should().Be("all");
        _push.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task StartAsync_FinishesPreviousPartyAndActiveEvents()
    {
        User admin = TestData.User("admin", UserRole.Admin);
        EventDefinition definition = TestData.Definition("quiz");
        EventSession session = TestData.Session(definition, admin.Id);

        _host.Db.Users.Add(admin);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        _host.Db.Parties.Add(new Party { Name = "Старая", Status = PartyStatus.Active });
        await _host.Db.SaveChangesAsync();

        await CreateService().StartAsync("Новая", resetBalances: false);

        List<Party> parties = await _host.DbAsync(db => db.Parties.OrderBy(p => p.StartedAt).ToListAsync());
        parties.Should().HaveCount(2);
        parties[0].Status.Should().Be(PartyStatus.Finished);
        parties[0].EndedAt.Should().NotBeNull();
        parties[1].Status.Should().Be(PartyStatus.Active);

        await _events.Received(1).FinishEventAsync(session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_FirstParty_ClosesActiveEvents()
    {
        User admin = TestData.User("admin", UserRole.Admin);
        EventDefinition definition = TestData.Definition("quiz");
        EventSession session = TestData.Session(definition, admin.Id);

        _host.Db.Users.Add(admin);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        await CreateService().StartAsync("Первая", resetBalances: false);

        await _events.Received(1).FinishEventAsync(session.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WithResetBalances_ZeroesWalletsAndWritesTransactions()
    {
        User rich = TestData.UserWithWallet("rich", balance: 100);
        User broke = TestData.UserWithWallet("broke", balance: 0);
        _host.Db.Users.AddRange(rich, broke);
        await _host.Db.SaveChangesAsync();

        await CreateService().StartAsync("Новая", resetBalances: true);

        List<Wallet> wallets = await _host.DbAsync(db =>
            db.Wallets.AsNoTracking().OrderBy(w => w.Balance).ToListAsync());

        wallets.Should().OnlyContain(w => w.Balance == 0);

        WalletTransaction reset = await _host.DbAsync(db => db.WalletTransactions
            .AsNoTracking()
            .SingleAsync(t => t.Type == WalletTransactionType.PartyReset));
        reset.Amount.Should().Be(-100);
        reset.WalletId.Should().Be(rich.Wallet.Id);

        await _pointsAward.Received(1).NotifyBalanceChangedAsync(
            rich.Id, 0, -100, "Новая вечеринка", Arg.Any<CancellationToken>());
        await _pointsAward.DidNotReceive().NotifyBalanceChangedAsync(
            broke.Id, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WithoutReset_KeepsBalances()
    {
        User rich = TestData.UserWithWallet("rich", balance: 100);
        _host.Db.Users.Add(rich);
        await _host.Db.SaveChangesAsync();

        await CreateService().StartAsync("Новая", resetBalances: false);

        int balance = await _host.DbAsync(db => db.Wallets.AsNoTracking().Select(w => w.Balance).SingleAsync());
        balance.Should().Be(100);
        (await _host.DbAsync(db => db.WalletTransactions.AsNoTracking().CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task FinishAsync_SetsFinishedAndBroadcastsSummary()
    {
        Party party = await CreateService().StartAsync("Смена", resetBalances: false);

        PartySummaryDto? summary = await CreateService().FinishAsync(party.Id);

        summary.Should().NotBeNull();
        summary!.Party.Status.Should().Be(nameof(PartyStatus.Finished));
        summary.Party.EndedAt.Should().NotBeNull();

        Party stored = await _host.DbAsync(db => db.Parties.AsNoTracking().SingleAsync());
        stored.Status.Should().Be(PartyStatus.Finished);
        stored.EndedAt.Should().NotBeNull();

        _hub.CallsFor("PartyFinished").Should().ContainSingle();
    }

    [Fact]
    public async Task FinishAsync_UnknownParty_ReturnsNull()
    {
        PartySummaryDto? summary = await CreateService().FinishAsync(Guid.NewGuid());

        summary.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentOrLatestAsync_PrefersActiveParty()
    {
        Party old = new() { Name = "Старая", Status = PartyStatus.Finished, StartedAt = DateTime.UtcNow.AddHours(-5), EndedAt = DateTime.UtcNow.AddHours(-1) };
        Party active = new() { Name = "Текущая", Status = PartyStatus.Active, StartedAt = DateTime.UtcNow };
        _host.Db.Parties.AddRange(old, active);
        await _host.Db.SaveChangesAsync();

        PartyService service = CreateService();

        (await service.GetCurrentOrLatestAsync()).Should().NotBeNull();
        (await service.GetCurrentOrLatestAsync())!.Id.Should().Be(active.Id);
    }

    [Fact]
    public async Task GetCurrentOrLatestAsync_WithoutActive_ReturnsLatestFinished()
    {
        Party older = new() { Name = "Старая", Status = PartyStatus.Finished, StartedAt = DateTime.UtcNow.AddHours(-10), EndedAt = DateTime.UtcNow.AddHours(-5) };
        Party latest = new() { Name = "Последняя", Status = PartyStatus.Finished, StartedAt = DateTime.UtcNow.AddHours(-4), EndedAt = DateTime.UtcNow.AddHours(-1) };
        _host.Db.Parties.AddRange(older, latest);
        await _host.Db.SaveChangesAsync();

        Party? current = await CreateService().GetCurrentOrLatestAsync();

        current!.Id.Should().Be(latest.Id);
    }

    [Fact]
    public async Task GetSummaryAsync_CountsPartyContentAndEarnings()
    {
        DateTime startedAt = DateTime.UtcNow.AddHours(-1);

        User admin = TestData.User("admin", UserRole.Admin);
        User player = TestData.UserWithWallet("player", balance: 120);
        EventDefinition definition = TestData.Definition("quiz");
        EventSession session = TestData.Session(definition, admin.Id);
        session.PartyId = Guid.NewGuid();

        Party party = new()
        {
            Id = session.PartyId.Value,
            Name = "Итоги",
            Status = PartyStatus.Active,
            StartedAt = startedAt
        };

        PlayerSubmission submission = new()
        {
            SessionId = session.Id,
            PlayerId = player.Id,
            Score = 30,
            SubmittedAt = DateTime.UtcNow
        };

        PartyPhoto photo = new()
        {
            PartyId = party.Id,
            UploadedById = player.Id,
            StoragePath = "photos/1.jpg",
            OriginalFileName = "1.jpg",
            ContentType = "image/jpeg",
            Status = ModerationStatus.Approved
        };

        PhotoLike firstLike = new() { PhotoId = photo.Id, UserId = admin.Id };
        PhotoLike secondLike = new() { PhotoId = photo.Id, UserId = player.Id };

        Wish wish = new() { PartyId = party.Id, PlayerId = player.Id, Text = "Ура!" };

        _host.Db.Users.AddRange(admin, player);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        _host.Db.Parties.Add(party);
        _host.Db.PlayerSubmissions.Add(submission);
        _host.Db.PartyPhotos.Add(photo);
        _host.Db.PhotoLikes.AddRange(firstLike, secondLike);
        _host.Db.Wishes.Add(wish);

        // Заработок внутри вечеринки, старая транзакция до старта и списание — не считаются
        _host.Db.WalletTransactions.AddRange(
            new WalletTransaction { WalletId = player.Wallet.Id, Amount = 30, Type = WalletTransactionType.EventReward, CreatedAt = DateTime.UtcNow },
            new WalletTransaction { WalletId = player.Wallet.Id, Amount = 100, Type = WalletTransactionType.AdminGrant, CreatedAt = startedAt.AddHours(-2) },
            new WalletTransaction { WalletId = player.Wallet.Id, Amount = -10, Type = WalletTransactionType.ShopPurchase, CreatedAt = DateTime.UtcNow });

        await _host.Db.SaveChangesAsync();

        PartySummaryDto? summary = await CreateService().GetSummaryAsync(party.Id);

        summary.Should().NotBeNull();
        summary!.EventsCount.Should().Be(1);
        summary.EventsByType.Should().ContainKey("quiz").WhoseValue.Should().Be(1);
        summary.SubmissionsCount.Should().Be(1);
        summary.PhotosCount.Should().Be(1);
        summary.WishesCount.Should().Be(1);
        summary.PlayersCount.Should().Be(1);
        summary.TopPlayers.Should().ContainSingle();
        summary.TopPlayers[0].DisplayName.Should().Be("player");
        summary.TopPlayers[0].Earned.Should().Be(30);
        summary.TopPhoto.Should().NotBeNull();
        summary.TopPhoto!.LikesCount.Should().Be(2);
        summary.DurationMinutes.Should().BeInRange(59, 61);
    }

    [Fact]
    public async Task GetSummaryAsync_UnknownParty_ReturnsNull()
    {
        (await CreateService().GetSummaryAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Schedule_AddsItemsInOrderAndFindsNext()
    {
        Party party = await CreateService().StartAsync("Смена", resetBalances: false);
        EventDefinition first = TestData.Definition("quiz", displayName: "Квиз");
        EventDefinition second = TestData.Definition("raffle", displayName: "Лототрон");
        _host.Db.EventDefinitions.AddRange(first, second);
        await _host.Db.SaveChangesAsync();

        PartyService service = CreateService();
        ScheduleItemDto firstItem = await service.AddScheduleItemAsync(party.Id, first);
        ScheduleItemDto secondItem = await service.AddScheduleItemAsync(party.Id, second);

        firstItem.Order.Should().Be(1);
        secondItem.Order.Should().Be(2);

        List<ScheduleItemDto> items = await service.GetScheduleAsync(party.Id);
        items.Select(i => i.DisplayName).Should().Equal("Квиз", "Лототрон");

        ScheduleItemDto? next = await service.GetNextScheduleItemAsync(party.Id);
        next!.Id.Should().Be(firstItem.Id);
    }

    [Fact]
    public async Task Schedule_MovesItemAndSkipsStartedWhenFindingNext()
    {
        Party party = await CreateService().StartAsync("Смена", resetBalances: false);
        EventDefinition first = TestData.Definition("quiz", displayName: "Квиз");
        EventDefinition second = TestData.Definition("raffle", displayName: "Лототрон");
        _host.Db.EventDefinitions.AddRange(first, second);
        await _host.Db.SaveChangesAsync();

        PartyService service = CreateService();
        ScheduleItemDto firstItem = await service.AddScheduleItemAsync(party.Id, first);
        ScheduleItemDto secondItem = await service.AddScheduleItemAsync(party.Id, second);

        (await service.MoveScheduleItemAsync(party.Id, secondItem.Id, moveUp: true)).Should().BeTrue();

        List<ScheduleItemDto> items = await service.GetScheduleAsync(party.Id);
        items.Select(i => i.DisplayName).Should().Equal("Лототрон", "Квиз");

        // Отмечаем первый как запущенный — «далее» становится второй
        await _host.DbAsync(async db =>
        {
            PartyScheduleItem stored = await db.PartyScheduleItems.SingleAsync(i => i.Id == secondItem.Id);
            stored.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });

        ScheduleItemDto? next = await service.GetNextScheduleItemAsync(party.Id);
        next!.Id.Should().Be(firstItem.Id);
    }

    [Fact]
    public async Task Schedule_RemovesItem()
    {
        Party party = await CreateService().StartAsync("Смена", resetBalances: false);
        EventDefinition definition = TestData.Definition("quiz");
        _host.Db.EventDefinitions.Add(definition);
        await _host.Db.SaveChangesAsync();

        PartyService service = CreateService();
        ScheduleItemDto item = await service.AddScheduleItemAsync(party.Id, definition);

        (await service.RemoveScheduleItemAsync(party.Id, item.Id)).Should().BeTrue();
        (await service.RemoveScheduleItemAsync(party.Id, item.Id)).Should().BeFalse();
        (await service.GetScheduleAsync(party.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Schedule_StartStartsEventAndMarksItem()
    {
        User admin = TestData.User("admin", UserRole.Admin);
        Party party = await CreateService().StartAsync("Смена", resetBalances: false);
        EventDefinition definition = TestData.Definition("quiz");
        _host.Db.Users.Add(admin);
        _host.Db.EventDefinitions.Add(definition);
        await _host.Db.SaveChangesAsync();

        PartyService service = CreateService();
        ScheduleItemDto item = await service.AddScheduleItemAsync(party.Id, definition);

        EventSession session = TestData.Session(definition, admin.Id);
        _events.StartEventAsync(definition.Id, admin.Id, Arg.Any<CancellationToken>()).Returns(session);

        ScheduleStartOutcome outcome = await service.StartScheduleItemAsync(party.Id, item.Id, admin.Id);

        outcome.Error.Should().BeNull();
        outcome.SessionId.Should().Be(session.Id);

        PartyScheduleItem stored = await _host.DbAsync(db =>
            db.PartyScheduleItems.AsNoTracking().SingleAsync(i => i.Id == item.Id));
        stored.StartedAt.Should().NotBeNull();
        stored.SessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task Schedule_StartTwice_ReturnsError()
    {
        User admin = TestData.User("admin", UserRole.Admin);
        Party party = await CreateService().StartAsync("Смена", resetBalances: false);
        EventDefinition definition = TestData.Definition("quiz");
        _host.Db.Users.Add(admin);
        _host.Db.EventDefinitions.Add(definition);
        await _host.Db.SaveChangesAsync();

        PartyService service = CreateService();
        ScheduleItemDto item = await service.AddScheduleItemAsync(party.Id, definition);

        EventSession session = TestData.Session(definition, admin.Id);
        _events.StartEventAsync(definition.Id, admin.Id, Arg.Any<CancellationToken>()).Returns(session);

        await service.StartScheduleItemAsync(party.Id, item.Id, admin.Id);
        ScheduleStartOutcome second = await service.StartScheduleItemAsync(party.Id, item.Id, admin.Id);

        second.Error.Should().Be("Этот пункт уже запускали");
    }

    [Fact]
    public async Task Schedule_StartOnFinishedParty_ReturnsError()
    {
        User admin = TestData.User("admin", UserRole.Admin);
        Party party = await CreateService().StartAsync("Смена", resetBalances: false);
        EventDefinition definition = TestData.Definition("quiz");
        _host.Db.Users.Add(admin);
        _host.Db.EventDefinitions.Add(definition);
        await _host.Db.SaveChangesAsync();

        PartyService service = CreateService();
        ScheduleItemDto item = await service.AddScheduleItemAsync(party.Id, definition);

        await service.FinishAsync(party.Id);

        ScheduleStartOutcome outcome = await service.StartScheduleItemAsync(party.Id, item.Id, admin.Id);

        outcome.Error.Should().Be("Вечеринка уже завершена");
    }
}
