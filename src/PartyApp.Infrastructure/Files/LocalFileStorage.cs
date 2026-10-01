using Microsoft.Extensions.Logging;

namespace PartyApp.Infrastructure.Files;

/// <summary>
/// Локальное хранилище: файлы лежат под UploadRoot и разложены по годам,
/// например photos/2026/ab12...jpg. Клиентское имя файла не используется,
/// расширение берётся из проверенного типа содержимого.
/// </summary>
public class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(string root, ILogger<LocalFileStorage> logger)
    {
        _root = Path.GetFullPath(root);
        _logger = logger;
    }

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
    {
        string safeExtension = NormalizeExtension(extension);
        string relativePath = Path.Combine(
            "photos",
            DateTime.UtcNow.Year.ToString(),
            $"{Guid.NewGuid():N}{safeExtension}");

        string fullPath = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using FileStream target = new(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(target, ct);

        return relativePath.Replace('\\', '/');
    }

    public Stream? OpenRead(string relativePath)
    {
        string fullPath = Resolve(relativePath);
        return File.Exists(fullPath) ? File.OpenRead(fullPath) : null;
    }

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public void Delete(string relativePath)
    {
        try
        {
            string fullPath = Resolve(relativePath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить файл хранилища {Path}", relativePath);
        }
    }

    /// <summary>Защита от path traversal: итоговый путь обязан остаться внутри UploadRoot.</summary>
    private string Resolve(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        string rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь выходит за пределы хранилища");

        return fullPath;
    }

    private static string NormalizeExtension(string extension)
    {
        string ext = extension.StartsWith('.') ? extension[1..] : extension;

        if (ext.Length is < 1 or > 5 || ext.Any(c => !char.IsLetterOrDigit(c)))
            throw new InvalidOperationException($"Недопустимое расширение файла: {extension}");

        return "." + ext.ToLowerInvariant();
    }
}