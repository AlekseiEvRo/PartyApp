using FluentAssertions;

using PartyApp.Api.Modules.Toast;

namespace PartyApp.UnitTests.Toast;

public class ToastStateTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsBusy_WithoutBusyUntil_IsFalse()
    {
        ToastState state = new();

        state.IsBusy(Now).Should().BeFalse();
    }

    [Fact]
    public void IsBusy_BusyUntilInFuture_IsTrue()
    {
        ToastState state = new() { BusyUntilUtc = Now.AddSeconds(30) };

        state.IsBusy(Now).Should().BeTrue();
    }

    [Fact]
    public void IsBusy_BusyUntilInPast_IsFalse()
    {
        ToastState state = new() { BusyUntilUtc = Now.AddSeconds(-1) };

        state.IsBusy(Now).Should().BeFalse();
    }

    [Fact]
    public void IsBusy_BusyUntilExactlyNow_IsFalse()
    {
        ToastState state = new() { BusyUntilUtc = Now };

        state.IsBusy(Now).Should().BeFalse();
    }
}