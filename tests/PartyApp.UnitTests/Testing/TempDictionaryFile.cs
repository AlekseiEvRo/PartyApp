using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.UnitTests.Testing;

/// <summary>
/// Временный файл словаря для тестов WordRush: создаётся в Temp и удаляется в Dispose.
/// </summary>
public sealed class TempDictionaryFile : IDisposable
{
    private readonly string _path;

    public TempDictionaryFile(params string[] words)
    {
        _path = Path.Combine(Path.GetTempPath(), $"party-dict-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(_path, words, Encoding.UTF8);
    }

    public RussianDictionaryService CreateService()
    {
        return new RussianDictionaryService(
            TestConfiguration.Create(("Dictionary:FilePath", _path)),
            NullLogger<RussianDictionaryService>.Instance);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}