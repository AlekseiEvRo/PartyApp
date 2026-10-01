using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Modules.Push;
using PartyApp.UnitTests.Testing;

using WebPush;

namespace PartyApp.UnitTests.Push;

public class PushNotificationServiceTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly string _directory;

    public PushNotificationServiceTests()
    {
        _host = new SqliteTestHost();
        _directory = Path.Combine(Path.GetTempPath(), "party-push-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string KeysPath => Path.Combine(_directory, "vapid.json");

    [Fact]
    public void PublicKey_FromConfiguredKeys_IsReturned()
    {
        VapidDetails generated = VapidHelper.GenerateVapidKeys();
        PushNotificationService service = new(
            _host.ScopeFactory,
            TestConfiguration.Create(
                ("Push:PublicKey", generated.PublicKey),
                ("Push:PrivateKey", generated.PrivateKey),
                ("Push:KeysFile", KeysPath)),
            NullLogger<PushNotificationService>.Instance);

        service.PublicKey.Should().Be(generated.PublicKey);
        File.Exists(KeysPath).Should().BeFalse("если ключи заданы в конфиге, файл не создаётся");
    }

    [Fact]
    public void PublicKey_FromKeysFile_IsReturned()
    {
        VapidDetails generated = VapidHelper.GenerateVapidKeys();
        File.WriteAllText(KeysPath, JsonSerializer.Serialize(new
        {
            Subject = "mailto:test@party-app.online",
            PublicKey = generated.PublicKey,
            PrivateKey = generated.PrivateKey
        }));

        PushNotificationService service = new(
            _host.ScopeFactory,
            TestConfiguration.Create(("Push:KeysFile", KeysPath)),
            NullLogger<PushNotificationService>.Instance);

        service.PublicKey.Should().Be(generated.PublicKey);
    }

    [Fact]
    public void PublicKey_WithoutKeys_GeneratesAndPersistsThem()
    {
        PushNotificationService service = new(
            _host.ScopeFactory,
            TestConfiguration.Create(("Push:KeysFile", KeysPath)),
            NullLogger<PushNotificationService>.Instance);

        service.PublicKey.Should().NotBeNullOrWhiteSpace();
        File.Exists(KeysPath).Should().BeTrue();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(KeysPath));
        document.RootElement.GetProperty("PublicKey").GetString().Should().Be(service.PublicKey);
        document.RootElement.GetProperty("PrivateKey").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void PublicKey_UsesPreviouslyGeneratedKeys_OnRestart()
    {
        PushNotificationService first = new(
            _host.ScopeFactory,
            TestConfiguration.Create(("Push:KeysFile", KeysPath)),
            NullLogger<PushNotificationService>.Instance);

        PushNotificationService second = new(
            _host.ScopeFactory,
            TestConfiguration.Create(("Push:KeysFile", KeysPath)),
            NullLogger<PushNotificationService>.Instance);

        second.PublicKey.Should().Be(first.PublicKey);
    }

    [Fact]
    public async Task SendToUserNowAsync_WithoutSubscriptions_ReturnsEmptyReport()
    {
        VapidDetails generated = VapidHelper.GenerateVapidKeys();
        PushNotificationService service = new(
            _host.ScopeFactory,
            TestConfiguration.Create(
                ("Push:PublicKey", generated.PublicKey),
                ("Push:PrivateKey", generated.PrivateKey)),
            NullLogger<PushNotificationService>.Instance);

        PushDeliveryReport report = await service.SendToUserNowAsync(
            Guid.NewGuid(),
            new PushMessage("Тест", "Тело", Tag: "test"));

        report.Should().Be(new PushDeliveryReport(0, 0, 0));
    }

    [Fact]
    public async Task SendToUsersAsync_WithEmptyRecipientList_DoesNothing()
    {
        PushNotificationService service = new(
            _host.ScopeFactory,
            TestConfiguration.Create(("Push:KeysFile", KeysPath)),
            NullLogger<PushNotificationService>.Instance);

        Func<Task> act = () => service.SendToUsersAsync(
            Array.Empty<Guid>(),
            new PushMessage("Тест"));

        await act.Should().NotThrowAsync();
    }
}