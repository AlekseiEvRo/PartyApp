using FluentAssertions;

using PartyApp.Api.Common.Security;

namespace PartyApp.UnitTests.Security;

public class PasswordGeneratorTests
{
    private const string Allowed = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    [Fact]
    public void Generate_ReturnsRequestedLength()
    {
        PasswordGenerator.Generate(12).Should().HaveLength(12);
    }

    [Fact]
    public void Generate_UsesOnlyUnambiguousCharacters()
    {
        string password = PasswordGenerator.Generate(50);

        password.All(c => Allowed.Contains(c)).Should().BeTrue();
        password.Should().NotContainAny("0", "O", "1", "l", "I");
    }

    [Fact]
    public void Generate_ReturnsDifferentPasswords()
    {
        PasswordGenerator.Generate().Should().NotBe(PasswordGenerator.Generate());
    }

    [Fact]
    public void Generate_WithTooShortLength_Throws()
    {
        Action act = () => PasswordGenerator.Generate(5);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
