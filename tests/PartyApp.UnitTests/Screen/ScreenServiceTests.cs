using FluentAssertions;

using PartyApp.Api.Modules.Screen;

namespace PartyApp.UnitTests.Screen;

public class ScreenServiceTests
{
    [Fact]
    public void GetState_InitiallyIdle()
    {
        ScreenService service = new();

        ScreenStateDto state = service.GetState();

        state.Mode.Should().Be(ScreenModes.Idle);
        state.SessionId.Should().BeNull();
        state.Message.Should().BeNull();
        state.Version.Should().Be(0);
    }

    [Fact]
    public void SetState_ChangesModeAndIncrementsVersion()
    {
        ScreenService service = new();

        ScreenStateDto first = service.SetState(ScreenModes.Leaderboard);
        ScreenStateDto second = service.SetState(ScreenModes.Message, message: "Танцуем!");

        first.Mode.Should().Be(ScreenModes.Leaderboard);
        first.Version.Should().Be(1);
        second.Mode.Should().Be(ScreenModes.Message);
        second.Message.Should().Be("Танцуем!");
        second.Version.Should().Be(2);
        service.GetState().Version.Should().Be(2);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void SetState_UnknownMode_Throws(string mode)
    {
        ScreenService service = new();

        Action act = () => service.SetState(mode);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SetState_NormalizesModeCase()
    {
        ScreenService service = new();

        ScreenStateDto state = service.SetState("LEADERBOARD");

        state.Mode.Should().Be(ScreenModes.Leaderboard);
    }

    [Fact]
    public void FireConfetti_IncrementsVersion()
    {
        ScreenService service = new();

        ConfettiDto first = service.FireConfetti();
        ConfettiDto second = service.FireConfetti();

        first.Version.Should().Be(1);
        second.Version.Should().Be(2);
    }

    [Fact]
    public void GetState_ReturnsFreshServerTime()
    {
        ScreenService service = new();

        service.GetState().ServerTimeUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }
}