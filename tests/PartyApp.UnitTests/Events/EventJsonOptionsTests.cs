using System.Text.Json;

using FluentAssertions;

using PartyApp.Api.Modules.Events.Handlers;

namespace PartyApp.UnitTests.Events;

public class EventJsonOptionsTests
{
    private sealed record Sample(string SecretWord, int PointsPerWord);

    [Fact]
    public void Serialize_UsesCamelCase()
    {
        string json = JsonSerializer.Serialize(
            new Sample("торт", 5),
            EventJsonOptions.Default);

        json.Should().Contain("\"secretWord\"").And.Contain("\"pointsPerWord\"");
    }

    [Fact]
    public void Deserialize_AcceptsCamelCase()
    {
        Sample? sample = JsonSerializer.Deserialize<Sample>(
            """{"secretWord":"торт","pointsPerWord":5}""",
            EventJsonOptions.Default);

        sample.Should().Be(new Sample("торт", 5));
    }

    [Fact]
    public void Deserialize_IgnoresPropertyNameCase()
    {
        Sample? sample = JsonSerializer.Deserialize<Sample>(
            """{"SECRETWORD":"торт","PointsPerWord":7}""",
            EventJsonOptions.Default);

        sample.Should().Be(new Sample("торт", 7));
    }
}