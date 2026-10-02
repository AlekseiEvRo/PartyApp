using FluentAssertions;
using Microsoft.Extensions.Configuration;

using PartyApp.Api.Common.Security;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Security;

public class JwtSigningKeyStoreTests : IDisposable
{
    private const string ValidKey = "ConfiguredSigningKeyThatIsLongEnoughForHmac_42!";

    private readonly string _directory;
    private readonly string _keysFile;

    public JwtSigningKeyStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "party-jwt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _keysFile = Path.Combine(_directory, "jwt.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Resolve_WithConfiguredKey_ReturnsItAndDoesNotTouchFile()
    {
        JwtSigningKey resolved = JwtSigningKeyStore.Resolve(TestConfiguration.Create(
            ("Jwt:SigningKey", ValidKey),
            ("Jwt:KeysFile", _keysFile)));

        resolved.Key.Should().Be(ValidKey);
        resolved.Source.Should().Be("конфиг");
        File.Exists(_keysFile).Should().BeFalse();
    }

    [Fact]
    public void Resolve_WithExistingFile_ReusesKey()
    {
        File.WriteAllText(_keysFile, $$"""{"SigningKey":"{{ValidKey}}"}""");

        JwtSigningKey resolved = JwtSigningKeyStore.Resolve(
            TestConfiguration.Create(("Jwt:KeysFile", _keysFile)));

        resolved.Key.Should().Be(ValidKey);
        resolved.Source.Should().Contain("jwt.json");
    }

    [Fact]
    public void Resolve_WithoutKey_GeneratesAndPersists()
    {
        IConfigurationRoot configuration = TestConfiguration.Create(("Jwt:KeysFile", _keysFile));

        JwtSigningKey first = JwtSigningKeyStore.Resolve(configuration);
        JwtSigningKey second = JwtSigningKeyStore.Resolve(configuration);

        first.Key.Should().HaveLength(88); // 64 байта в Base64
        first.Source.Should().Contain("сгенерирован");
        second.Key.Should().Be(first.Key);
        File.Exists(_keysFile).Should().BeTrue();
    }

    [Fact]
    public void Resolve_WithTooShortKey_Throws()
    {
        Action act = () => JwtSigningKeyStore.Resolve(
            TestConfiguration.Create(("Jwt:SigningKey", "short")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*слишком короткий*");
    }

    [Fact]
    public void Resolve_WithBrokenFile_Throws()
    {
        File.WriteAllText(_keysFile, "{not json");

        Action act = () => JwtSigningKeyStore.Resolve(
            TestConfiguration.Create(("Jwt:KeysFile", _keysFile)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Не удалось прочитать*");
    }
}
