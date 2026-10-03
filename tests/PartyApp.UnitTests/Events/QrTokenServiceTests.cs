using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events;

public class QrTokenServiceTests : IDisposable
{
    private const string AllowedCodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;

    private readonly SqliteTestHost _host;
    private readonly QrTokenService _service;

    public QrTokenServiceTests()
    {
        _host = new SqliteTestHost();
        _service = new QrTokenService(_host.ScopeFactory, NullLogger<QrTokenService>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(1000)]
    public async Task GenerateTokensAsync_WithOutOfRangeCount_Throws(int count)
    {
        Func<Task> act = () => _service.GenerateTokensAsync(count, points: 10);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Количество токенов должно быть от 1 до 100");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task GenerateTokensAsync_WithValidCount_ReturnsAndPersistsTokens(int count)
    {
        List<QrTokenDto> tokens = await _service.GenerateTokensAsync(count, points: 15);

        tokens.Should().HaveCount(count);
        tokens.Should().OnlyContain(t => t.Points == 15);
        tokens.Should().OnlyContain(t => !t.IsRedeemed && t.RedeemedAt == null);
        tokens.Select(t => t.Code).Should().OnlyHaveUniqueItems();
        (await _host.Db.QrTokens.CountAsync()).Should().Be(count);
    }

    [Fact]
    public async Task GenerateTokensAsync_CodesHaveSafeFormat()
    {
        List<QrTokenDto> tokens = await _service.GenerateTokensAsync(100, points: 5);

        foreach (QrTokenDto token in tokens)
        {
            token.Code.Should().HaveLength(CodeLength);
            token.Code.Should().MatchRegex($"^[{AllowedCodeChars}]+$");
            token.Code.Should().NotContainAny("0", "O", "1", "I");
        }
    }

    [Fact]
    public async Task GenerateTokensAsync_DoesNotDuplicateExistingCodes()
    {
        List<QrTokenDto> firstBatch = await _service.GenerateTokensAsync(50, points: 5);
        List<QrTokenDto> secondBatch = await _service.GenerateTokensAsync(50, points: 5);

        firstBatch.Concat(secondBatch)
            .Select(t => t.Code)
            .Should().OnlyHaveUniqueItems();

        (await _host.Db.QrTokens.CountAsync()).Should().Be(100);
    }

    [Fact]
    public async Task GetTokensAsync_ReturnsEmptyListForFreshDatabase()
    {
        (await _service.GetTokensAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetTokensAsync_MapsAllFieldsAndOrdersByCreatedAt()
    {
        User player = TestData.User("alice");
        _host.Db.Users.Add(player);

        QrToken redeemed = TestData.QrToken("CODE11", points: 30);
        redeemed.RedeemedAt = DateTime.UtcNow;
        redeemed.RedeemedById = player.Id;
        QrToken active = TestData.QrToken("CODE22", points: 10);

        active.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        _host.Db.QrTokens.AddRange(redeemed, active);
        await _host.Db.SaveChangesAsync();

        List<QrTokenDto> tokens = await _service.GetTokensAsync();

        tokens.Should().HaveCount(2);
        tokens[0].Code.Should().Be("CODE22");
        tokens[0].Points.Should().Be(10);
        tokens[0].IsRedeemed.Should().BeFalse();
        tokens[0].RedeemedAt.Should().BeNull();
        tokens[0].RedeemedByName.Should().BeNull();

        tokens[1].Code.Should().Be("CODE11");
        tokens[1].IsRedeemed.Should().BeTrue();
        tokens[1].RedeemedAt.Should().NotBeNull();
        tokens[1].RedeemedByName.Should().Be("alice");
    }

    [Fact]
    public async Task GetStatsAsync_OnEmptyDatabase_ReturnsZeros()
    {
        QrStatsDto stats = await _service.GetStatsAsync();

        stats.Total.Should().Be(0);
        stats.Redeemed.Should().Be(0);
        stats.Remaining.Should().Be(0);
        stats.Players.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_CountsTotalRedeemedAndGroupsByPlayer()
    {
        User alice = TestData.User("alice", displayName: "Алиса");
        User bob = TestData.User("bob", displayName: "Боб");
        _host.Db.Users.AddRange(alice, bob);

        QrToken aliceFirst = TestData.QrToken("AAAA11", points: 10);
        aliceFirst.RedeemedById = alice.Id;
        aliceFirst.RedeemedAt = DateTime.UtcNow;

        QrToken aliceSecond = TestData.QrToken("AAAA22", points: 10);
        aliceSecond.RedeemedById = alice.Id;
        aliceSecond.RedeemedAt = DateTime.UtcNow;

        QrToken bobFirst = TestData.QrToken("BBBB11", points: 20);
        bobFirst.RedeemedById = bob.Id;
        bobFirst.RedeemedAt = DateTime.UtcNow;

        QrToken available = TestData.QrToken("FREE11", points: 5);

        _host.Db.QrTokens.AddRange(aliceFirst, aliceSecond, bobFirst, available);
        await _host.Db.SaveChangesAsync();

        QrStatsDto stats = await _service.GetStatsAsync();

        stats.Total.Should().Be(4);
        stats.Redeemed.Should().Be(3);
        stats.Remaining.Should().Be(1);

        stats.Players.Should().HaveCount(2);
        stats.Players[0].PlayerId.Should().Be(alice.Id);
        stats.Players[0].DisplayName.Should().Be("Алиса");
        stats.Players[0].Count.Should().Be(2);
        stats.Players[1].PlayerId.Should().Be(bob.Id);
        stats.Players[1].Count.Should().Be(1);
    }
}