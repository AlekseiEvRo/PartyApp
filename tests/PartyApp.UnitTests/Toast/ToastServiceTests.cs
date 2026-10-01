using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using PartyApp.Api.Modules.Toast;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Toast;

public class ToastServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly RecordingHubContext _hub = new();
    private readonly FakePushNotificationService _push = new();
    private readonly FakeTimeProvider _timeProvider = new(Start);

    private ToastService CreateService(params (string Key, string? Value)[] config)
    {
        Dictionary<string, string?> values = new()
        {
            ["Toast:CooldownSeconds"] = "30",
            ["Toast:Points"] = "1"
        };

        foreach ((string key, string? value) in config)
            values[key] = value;

        return new ToastService(
            _hub,
            _award,
            _push,
            TestConfiguration.Create(values.Select(v => (v.Key, v.Value)).ToArray()),
            _timeProvider,
            NullLogger<ToastService>.Instance);
    }

    private static JsonElement Json(object? value)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;
    }

    [Fact]
    public async Task TrySayToast_FirstToast_AwardsPoints()
    {
        ToastService service = CreateService();
        Guid userId = Guid.NewGuid();

        ToastResult result = await service.TrySayToastAsync(userId, "Алиса");

        result.Success.Should().BeTrue();
        result.Points.Should().Be(1);
        result.Message.Should().Be("Тост засчитан!");
        result.BusyByName.Should().BeNull();
        result.BusyUntilUtc.Should().Be(Start.AddSeconds(30).UtcDateTime);

        await _award.Received(1).AwardAsync(
            userId,
            1,
            "Тост за именинника",
            Arg.Any<WalletTransactionType>(),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySayToast_FirstToast_BroadcastsAndPushesToEveryoneExceptSpeaker()
    {
        ToastService service = CreateService();
        Guid userId = Guid.NewGuid();

        await service.TrySayToastAsync(userId, "Алиса");

        RecordingHubContext.HubCall call = _hub.SingleCall("ToastStarted");
        call.Target.Should().Be("all");
        JsonElement payload = Json(call.Payload);
        payload.GetProperty("userId").GetGuid().Should().Be(userId);
        payload.GetProperty("username").GetString().Should().Be("Алиса");

        PushCall push = _push.Calls.Should().ContainSingle().Subject;
        push.ExcludedUserId.Should().Be(userId);
        push.Message.Title.Should().Be("🍾 Тосты!");
        push.Message.Body.Should().Be("Алиса говорит тост — скорее слушай!");
        push.Message.Tag.Should().Be("toast");
    }

    [Fact]
    public async Task TrySayToast_DuringCooldown_ReturnsBusyWithoutSideEffects()
    {
        ToastService service = CreateService();
        Guid first = Guid.NewGuid();

        await service.TrySayToastAsync(first, "Алиса");
        ToastResult result = await service.TrySayToastAsync(Guid.NewGuid(), "Борис");

        result.Success.Should().BeFalse();
        result.Points.Should().Be(0);
        result.Message.Should().Be("Тост уже говорит Алиса");
        result.BusyByName.Should().Be("Алиса");
        result.BusyUntilUtc.Should().Be(Start.AddSeconds(30).UtcDateTime);

        await _award.Received(1).AwardAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        _hub.CallsFor("ToastStarted").Should().HaveCount(1);
        _push.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task TrySayToast_AfterCooldown_AllowsNextSpeaker()
    {
        ToastService service = CreateService();
        await service.TrySayToastAsync(Guid.NewGuid(), "Алиса");

        _timeProvider.Advance(TimeSpan.FromSeconds(31));
        ToastResult result = await service.TrySayToastAsync(Guid.NewGuid(), "Борис");

        result.Success.Should().BeTrue();
        await _award.Received(2).AwardAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySayToast_AtExactCooldownBoundary_IsAllowed()
    {
        ToastService service = CreateService();
        await service.TrySayToastAsync(Guid.NewGuid(), "Алиса");

        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        ToastResult result = await service.TrySayToastAsync(Guid.NewGuid(), "Борис");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TrySayToast_UsesCustomConfig()
    {
        ToastService service = CreateService(
            ("Toast:CooldownSeconds", "10"),
            ("Toast:Points", "5"));

        ToastResult result = await service.TrySayToastAsync(Guid.NewGuid(), "Алиса");
        ToastResult blocked = await service.TrySayToastAsync(Guid.NewGuid(), "Борис");

        result.Points.Should().Be(5);
        result.BusyUntilUtc.Should().Be(Start.AddSeconds(10).UtcDateTime);
        blocked.Message.Should().Be("Тост уже говорит Алиса");
        await _award.Received(1).AwardAsync(
            Arg.Any<Guid>(), 5, Arg.Any<string>(),
            Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySayToast_WithoutConfig_UsesDefaults()
    {
        ToastService service = CreateService(
            ("Toast:CooldownSeconds", null),
            ("Toast:Points", null));

        ToastResult result = await service.TrySayToastAsync(Guid.NewGuid(), "Алиса");

        result.Points.Should().Be(1);
        result.BusyUntilUtc.Should().Be(Start.AddSeconds(60).UtcDateTime);
    }

    [Fact]
    public async Task TrySayToast_WhenAwardFails_ResetsStateAndRethrows()
    {
        ToastService service = CreateService();
        _award.AwardAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("база недоступна")));

        Func<Task> act = () => service.TrySayToastAsync(Guid.NewGuid(), "Алиса");

        await act.Should().ThrowAsync<InvalidOperationException>();
        service.GetStatus().IsBusy.Should().BeFalse();
        _hub.Calls.Should().BeEmpty();
        _push.Calls.Should().BeEmpty();

        _award.AwardAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(1);
        ToastResult retry = await service.TrySayToastAsync(Guid.NewGuid(), "Борис");
        retry.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TrySayToast_TwoConcurrentCalls_OnlyOneWins()
    {
        ToastService service = CreateService();

        ToastResult[] results = await Task.WhenAll(
            service.TrySayToastAsync(Guid.NewGuid(), "Алиса"),
            service.TrySayToastAsync(Guid.NewGuid(), "Борис"));

        results.Should().ContainSingle(r => r.Success);
        results.Should().ContainSingle(r => !r.Success);
        _hub.CallsFor("ToastStarted").Should().HaveCount(1);
        await _award.Received(1).AwardAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<WalletTransactionType>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GetStatus_WithoutToast_ReturnsNotBusy()
    {
        ToastStatus status = CreateService().GetStatus();

        status.IsBusy.Should().BeFalse();
        status.BusyUntilUtc.Should().BeNull();
        status.CurrentSpeakerName.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_DuringToast_ReturnsSpeakerAndUntil()
    {
        ToastService service = CreateService();
        await service.TrySayToastAsync(Guid.NewGuid(), "Алиса");

        ToastStatus status = service.GetStatus();

        status.IsBusy.Should().BeTrue();
        status.BusyUntilUtc.Should().Be(Start.AddSeconds(30).UtcDateTime);
        status.CurrentSpeakerName.Should().Be("Алиса");
    }

    [Fact]
    public async Task GetStatus_AfterCooldown_ReturnsNotBusy()
    {
        ToastService service = CreateService();
        await service.TrySayToastAsync(Guid.NewGuid(), "Алиса");

        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        service.GetStatus().IsBusy.Should().BeFalse();
    }
}