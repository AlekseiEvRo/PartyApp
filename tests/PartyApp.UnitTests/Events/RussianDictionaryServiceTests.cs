using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events;

public class RussianDictionaryServiceTests : IDisposable
{
    private readonly string _directory;

    public RussianDictionaryServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "party-dictionary-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private RussianDictionaryService CreateService(string content, string fileName = "russian.txt")
    {
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);

        return new RussianDictionaryService(
            TestConfiguration.Create(("Dictionary:FilePath", path)),
            NullLogger<RussianDictionaryService>.Instance);
    }

    [Fact]
    public void WordExists_KnownWord_ReturnsTrue()
    {
        RussianDictionaryService service = CreateService("торт\nподарок\nсвеча");

        service.WordExists("торт").Should().BeTrue();
        service.WordExists("подарок").Should().BeTrue();
    }

    [Fact]
    public void WordExists_UnknownWord_ReturnsFalse()
    {
        RussianDictionaryService service = CreateService("торт\nподарок");

        service.WordExists("крокодил").Should().BeFalse();
    }

    [Fact]
    public void WordExists_IgnoresLetterCase()
    {
        RussianDictionaryService service = CreateService("торт");

        service.WordExists("ТОРТ").Should().BeTrue();
        service.WordExists("Торт").Should().BeTrue();
    }

    [Fact]
    public void WordExists_TreatsYoAndYeAsSameLetter()
    {
        RussianDictionaryService service = CreateService("ёжик");

        service.WordExists("ежик").Should().BeTrue();
        service.WordExists("ЁЖИК").Should().BeTrue();
    }

    [Fact]
    public void WordExists_TrimsWordsFromFile()
    {
        RussianDictionaryService service = CreateService("  торт  \n\tподарок\t");

        service.WordExists("торт").Should().BeTrue();
        service.WordExists("подарок").Should().BeTrue();
    }

    [Fact]
    public void WordExists_IgnoresBlankLines()
    {
        RussianDictionaryService service = CreateService("\n   \nторт\n\n");

        service.WordExists("торт").Should().BeTrue();
        service.WordExists("").Should().BeFalse();
    }

    [Fact]
    public void WordExists_WhenDictionaryFileMissing_ReturnsFalse()
    {
        RussianDictionaryService service = new(
            TestConfiguration.Create(("Dictionary:FilePath", Path.Combine(_directory, "does-not-exist.txt"))),
            NullLogger<RussianDictionaryService>.Instance);

        service.WordExists("торт").Should().BeFalse();
    }

    [Fact]
    public void Constructor_WhenFilePathNotConfigured_DoesNotThrow()
    {
        Action act = () => new RussianDictionaryService(
            TestConfiguration.Empty(),
            NullLogger<RussianDictionaryService>.Instance);

        act.Should().NotThrow();
    }
}