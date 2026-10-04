using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Polls;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Polls;

public class PollServiceTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly RecordingHubContext _hub = new();
    private readonly IEventService _events = Substitute.For<IEventService>();
    private readonly PollService _service;
    private readonly Guid _adminId;

    public PollServiceTests()
    {
        _host = new SqliteTestHost();
        _service = new PollService(_host.Db, _events, _hub, NullLogger<PollService>.Instance);

        // Голосование ссылается на создателя, поэтому админ должен быть в БД
        User admin = TestData.User("admin", UserRole.Admin);
        _adminId = admin.Id;
        _host.Db.Users.Add(admin);
        _host.Db.SaveChanges();
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private async Task<EventDefinition> SeedDefinitionAsync(string displayName)
    {
        EventDefinition definition = TestData.Definition("quiz", displayName: displayName);
        _host.Db.EventDefinitions.Add(definition);
        await _host.Db.SaveChangesAsync();
        return definition;
    }

    private async Task<User> SeedUserAsync()
    {
        User user = TestData.User($"u-{Guid.NewGuid():N}"[..10]);
        _host.Db.Users.Add(user);
        await _host.Db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task CreateAsync_AddsOptionsAndBroadcasts()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");

        PollDto poll = await _service.CreateAsync("Что дальше?", new[] { first.Id, second.Id }, _adminId);

        poll.Status.Should().Be(nameof(PollStatus.Open));
        poll.Options.Select(o => o.DisplayName).Should().Equal("Квиз", "Лототрон");
        poll.Options.Should().OnlyContain(o => o.Votes == 0);

        _hub.SingleCall("PollUpdated").Target.Should().Be("all");
    }

    [Fact]
    public async Task CreateAsync_ClosesPreviousOpenPoll()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");

        PollDto previous = await _service.CreateAsync("Старое", new[] { first.Id, second.Id }, _adminId);
        await _service.CreateAsync("Новое", new[] { first.Id, second.Id }, _adminId);

        Poll stored = await _host.DbAsync(db => db.Polls.AsNoTracking().SingleAsync(p => p.Id == previous.Id));
        stored.Status.Should().Be(PollStatus.Closed);
        stored.ClosedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task VoteAsync_AddsVoteAndCounts()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");
        User player = await SeedUserAsync();

        PollDto poll = await _service.CreateAsync("Что дальше?", new[] { first.Id, second.Id }, _adminId);

        (PollDto? updated, string? error) = await _service.VoteAsync(poll.Id, player.Id, poll.Options[1].Id);

        error.Should().BeNull();
        updated!.MyOptionId.Should().Be(poll.Options[1].Id);
        updated.TotalVotes.Should().Be(1);
        updated.Options[1].Votes.Should().Be(1);
        updated.Options[0].Votes.Should().Be(0);
    }

    [Fact]
    public async Task VoteAsync_RevoteChangesOption()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");
        User player = await SeedUserAsync();

        PollDto poll = await _service.CreateAsync("Что дальше?", new[] { first.Id, second.Id }, _adminId);
        await _service.VoteAsync(poll.Id, player.Id, poll.Options[0].Id);

        (PollDto? updated, string? error) = await _service.VoteAsync(poll.Id, player.Id, poll.Options[1].Id);

        error.Should().BeNull();
        updated!.TotalVotes.Should().Be(1);
        updated.Options[0].Votes.Should().Be(0);
        updated.Options[1].Votes.Should().Be(1);
        updated.MyOptionId.Should().Be(poll.Options[1].Id);
    }

    [Fact]
    public async Task VoteAsync_AfterClose_ReturnsError()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");
        User player = await SeedUserAsync();

        PollDto poll = await _service.CreateAsync("Что дальше?", new[] { first.Id, second.Id }, _adminId);
        await _service.CloseAsync(poll.Id);

        (PollDto? updated, string? error) = await _service.VoteAsync(poll.Id, player.Id, poll.Options[0].Id);

        updated.Should().BeNull();
        error.Should().Be("Голосование уже закрыто");
    }

    [Fact]
    public async Task VoteAsync_InvalidOption_ReturnsError()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");
        User player = await SeedUserAsync();

        PollDto poll = await _service.CreateAsync("Что дальше?", new[] { first.Id, second.Id }, _adminId);

        (PollDto? updated, string? error) = await _service.VoteAsync(poll.Id, player.Id, Guid.NewGuid());

        updated.Should().BeNull();
        error.Should().Be("Такого варианта нет");
    }

    [Fact]
    public async Task CloseAsync_SetsStatusAndBroadcasts()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");

        PollDto poll = await _service.CreateAsync("Что дальше?", new[] { first.Id, second.Id }, _adminId);
        (PollDto? closed, string? error) = await _service.CloseAsync(poll.Id);

        error.Should().BeNull();
        closed!.Status.Should().Be(nameof(PollStatus.Closed));
        closed.ClosedAt.Should().NotBeNull();
        _hub.SingleCall("PollClosed").Target.Should().Be("all");
    }

    [Fact]
    public async Task CloseAsync_Unknown_ReturnsError()
    {
        (PollDto? poll, string? error) = await _service.CloseAsync(Guid.NewGuid());

        poll.Should().BeNull();
        error.Should().Be("Голосование не найдено");
    }

    [Fact]
    public async Task StartWinnerAsync_StartsWinningDefinitionAndClosesPoll()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");
        User firstPlayer = await SeedUserAsync();
        User secondPlayer = await SeedUserAsync();
        User thirdPlayer = await SeedUserAsync();

        PollDto poll = await _service.CreateAsync("Что дальше?", new[] { first.Id, second.Id }, _adminId);
        await _service.VoteAsync(poll.Id, firstPlayer.Id, poll.Options[0].Id);
        await _service.VoteAsync(poll.Id, secondPlayer.Id, poll.Options[0].Id);
        await _service.VoteAsync(poll.Id, thirdPlayer.Id, poll.Options[1].Id);

        Guid adminId = Guid.NewGuid();
        EventSession session = TestData.Session(first, adminId);
        _events.StartEventAsync(first.Id, adminId, Arg.Any<CancellationToken>()).Returns(session);

        (Guid? sessionId, string? error) = await _service.StartWinnerAsync(poll.Id, adminId);

        error.Should().BeNull();
        sessionId.Should().Be(session.Id);

        await _events.Received(1).StartEventAsync(first.Id, adminId, Arg.Any<CancellationToken>());

        Poll stored = await _host.DbAsync(db => db.Polls.AsNoTracking().SingleAsync(p => p.Id == poll.Id));
        stored.Status.Should().Be(PollStatus.Closed);
    }

    [Fact]
    public async Task StartWinnerAsync_WithoutVotes_ReturnsError()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");

        PollDto poll = await _service.CreateAsync("Что дальше?", new[] { first.Id, second.Id }, _adminId);

        (Guid? sessionId, string? error) = await _service.StartWinnerAsync(poll.Id, _adminId);

        sessionId.Should().BeNull();
        error.Should().Be("В голосовании пока нет голосов");
    }

    [Fact]
    public async Task GetCurrentAsync_ReturnsLatestPollWithMyVote()
    {
        EventDefinition first = await SeedDefinitionAsync("Квиз");
        EventDefinition second = await SeedDefinitionAsync("Лототрон");
        User player = await SeedUserAsync();

        await _service.CreateAsync("Старое", new[] { first.Id, second.Id }, _adminId);
        PollDto latest = await _service.CreateAsync("Новое", new[] { first.Id, second.Id }, _adminId);
        await _service.VoteAsync(latest.Id, player.Id, latest.Options[1].Id);

        PollDto? current = await _service.GetCurrentAsync(player.Id);

        current!.Id.Should().Be(latest.Id);
        current.Question.Should().Be("Новое");
        current.MyOptionId.Should().Be(latest.Options[1].Id);
    }

    [Fact]
    public async Task GetCurrentAsync_WithoutPolls_ReturnsNull()
    {
        (await _service.GetCurrentAsync(Guid.NewGuid())).Should().BeNull();
    }
}
