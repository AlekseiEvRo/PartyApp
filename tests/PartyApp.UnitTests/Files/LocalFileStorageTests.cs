using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Infrastructure.Files;

namespace PartyApp.UnitTests.Files;

public class LocalFileStorageTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "party-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(_root, NullLogger<LocalFileStorage>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_WritesFileInsideRoot_AndReturnsRelativePath()
    {
        using MemoryStream content = new("данные"u8.ToArray());

        string path = await _storage.SaveAsync(content, ".jpg");

        path.Should().StartWith("photos/");
        path.Should().EndWith(".jpg");
        _storage.Exists(path).Should().BeTrue();

        await using Stream stream = _storage.OpenRead(path)!;
        using StreamReader reader = new(stream);
        (await reader.ReadToEndAsync()).Should().Be("данные");
    }

    [Fact]
    public async Task SaveAsync_GeneratesUniquePaths()
    {
        using MemoryStream first = new(new byte[] { 1, 2, 3 });
        using MemoryStream second = new(new byte[] { 4, 5, 6 });

        string pathA = await _storage.SaveAsync(first, ".png");
        string pathB = await _storage.SaveAsync(second, ".png");

        pathA.Should().NotBe(pathB);
        _storage.Exists(pathA).Should().BeTrue();
        _storage.Exists(pathB).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("jp g")]
    [InlineData("jpg.exe")]
    public async Task SaveAsync_InvalidExtension_Throws(string extension)
    {
        using MemoryStream content = new(new byte[] { 1 });

        Func<Task> act = () => _storage.SaveAsync(content, extension);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void OpenRead_MissingFile_ReturnsNull()
    {
        _storage.OpenRead("photos/2099/missing.jpg").Should().BeNull();
    }

    [Fact]
    public void OpenRead_PathTraversal_Throws()
    {
        Action act = () => _storage.OpenRead("../outside.txt");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_RemovesFile_AndIsIdempotent()
    {
        using MemoryStream content = new(new byte[] { 1, 2 });
        string path = await _storage.SaveAsync(content, ".webp");

        _storage.Delete(path);
        _storage.Exists(path).Should().BeFalse();

        Action secondDelete = () => _storage.Delete(path);
        secondDelete.Should().NotThrow();
    }

    [Fact]
    public async Task SaveAsync_WithFolder_WritesIntoThatFolder()
    {
        using MemoryStream content = new(new byte[] { 1, 2, 3 });

        string path = await _storage.SaveAsync(content, ".jpg", "avatars");

        path.Should().StartWith("avatars/");
        _storage.Exists(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("photos/../evil")]
    [InlineData("a b")]
    [InlineData("фото")]
    public async Task SaveAsync_InvalidFolder_Throws(string folder)
    {
        using MemoryStream content = new(new byte[] { 1 });

        Func<Task> act = () => _storage.SaveAsync(content, ".jpg", folder);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}