using FluentAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Persistence;

public class AppDbContextTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
    }

    [Fact]
    public async Task Users_UsernameIsUnique()
    {
        _host.Db.Users.Add(TestData.User("alice"));
        await _host.Db.SaveChangesAsync();

        _host.Db.Users.Add(TestData.User("alice"));

        Func<Task> act = () => _host.Db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Wallets_UserIdIsUnique()
    {
        User user = TestData.User("alice");
        _host.Db.Users.Add(user);
        await _host.Db.SaveChangesAsync();

        _host.Db.Wallets.Add(new Wallet { UserId = user.Id, Balance = 1 });
        await _host.Db.SaveChangesAsync();

        // Второй кошелёк тому же пользователю вставляем мимо EF:
        // трекер сам не даёт привязать два Wallet к одному User,
        // поэтому здесь проверяется именно уникальный индекс в БД.
        Func<Task> act = () => _host.Db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Wallets\" (\"Id\", \"UserId\", \"Balance\") VALUES ({0}, {1}, {2})",
            Guid.NewGuid(), user.Id, 2);

        await act.Should().ThrowAsync<SqliteException>()
            .WithMessage("*UNIQUE constraint failed*");
    }

    [Fact]
    public async Task QrTokens_CodeIsUnique()
    {
        _host.Db.QrTokens.Add(TestData.QrToken("ABC234"));
        await _host.Db.SaveChangesAsync();

        _host.Db.QrTokens.Add(TestData.QrToken("ABC234"));

        Func<Task> act = () => _host.Db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task PushSubscriptions_EndpointIsUnique()
    {
        User user = TestData.User("alice");
        _host.Db.Users.Add(user);
        await _host.Db.SaveChangesAsync();

        _host.Db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://fcm.googleapis.com/fcm/send/abc",
            P256dh = "key",
            Auth = "auth"
        });
        await _host.Db.SaveChangesAsync();

        _host.Db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://fcm.googleapis.com/fcm/send/abc",
            P256dh = "another",
            Auth = "another"
        });

        Func<Task> act = () => _host.Db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task DeletingUser_CascadesWalletAndPushSubscriptions()
    {
        User user = TestData.UserWithWallet("alice", balance: 10);
        _host.Db.Users.Add(user);
        _host.Db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://fcm.googleapis.com/fcm/send/abc",
            P256dh = "key",
            Auth = "auth"
        });
        await _host.Db.SaveChangesAsync();

        _host.Db.Users.Remove(user);
        await _host.Db.SaveChangesAsync();

        (await _host.Db.Wallets.AsNoTracking().CountAsync()).Should().Be(0);
        (await _host.Db.PushSubscriptions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DateTimeProperties_AreReadBackAsUtc()
    {
        DateTime unspecified = new(2026, 10, 1, 12, 30, 45, DateTimeKind.Unspecified);
        EventDefinition definition = TestData.Definition("quiz");
        definition.CreatedAt = unspecified;

        User admin = TestData.User("admin", UserRole.Admin);
        _host.Db.Users.Add(admin);
        _host.Db.EventDefinitions.Add(definition);
        await _host.Db.SaveChangesAsync();

        EventSession session = TestData.Session(definition, admin.Id);
        session.StartedAt = unspecified;
        session.EndedAt = unspecified;
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        EventDefinition reloadedDefinition = await _host.Db.EventDefinitions
            .AsNoTracking()
            .SingleAsync(d => d.Id == definition.Id);
        reloadedDefinition.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        reloadedDefinition.CreatedAt.Should().Be(unspecified);

        EventSession reloadedSession = await _host.Db.EventSessions
            .AsNoTracking()
            .SingleAsync(s => s.Id == session.Id);
        reloadedSession.StartedAt.Kind.Should().Be(DateTimeKind.Utc);
        reloadedSession.EndedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        reloadedSession.StartedAt.Should().Be(unspecified);
    }
}