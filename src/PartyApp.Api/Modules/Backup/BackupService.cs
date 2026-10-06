using System.Data;
using System.IO.Compression;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Backup;

/// <summary>
/// Создаёт zip-архив с резервной копией: база (через SQLite Online Backup API),
/// VAPID/JWT-ключи и загруженные фото. Старые архивы ротируются.
/// </summary>
public class BackupService
{
    private const string ArchivePrefix = "party-backup-";
    private const string ArchiveMask = "party-backup-*.zip";

    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BackupService> _logger;

    public BackupService(AppDbContext db, IConfiguration configuration, ILogger<BackupService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> CreateAsync(CancellationToken ct = default)
    {
        string directory = ResolveDirectory();
        Directory.CreateDirectory(directory);

        string archivePath = Path.Combine(directory, $"{ArchivePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.zip");
        string databaseCopyPath = Path.Combine(Path.GetTempPath(), $"party-backup-{Guid.NewGuid():N}.db");

        try
        {
            await CopyDatabaseAsync(databaseCopyPath, ct);
            CreateArchive(archivePath, databaseCopyPath);
        }
        finally
        {
            TryDelete(databaseCopyPath);
        }

        Rotate(directory);
        _logger.LogInformation("Резервная копия создана: {Path}", archivePath);

        return archivePath;
    }

    public IReadOnlyList<BackupFile> List()
    {
        string directory = ResolveDirectory();
        if (!Directory.Exists(directory))
            return Array.Empty<BackupFile>();

        return new DirectoryInfo(directory)
            .GetFiles(ArchiveMask)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    private string ResolveDirectory()
    {
        return _configuration["Backup:Directory"] ?? "App_Data/backups";
    }

    /// <summary>Копирует базу средствами SQLite: простое копирование файла может дать битый снимок.</summary>
    private async Task CopyDatabaseAsync(string destinationPath, CancellationToken ct)
    {
        SqliteConnection source = (SqliteConnection)_db.Database.GetDbConnection();

        bool openedHere = false;
        if (source.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(ct);
            openedHere = true;
        }

        try
        {
            using SqliteConnection destination = new($"Data Source={destinationPath};Pooling=False");
            await destination.OpenAsync(ct);
            source.BackupDatabase(destination);
        }
        finally
        {
            if (openedHere)
                await _db.Database.CloseConnectionAsync();
        }
    }

    private void CreateArchive(string archivePath, string databaseCopyPath)
    {
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        archive.CreateEntryFromFile(databaseCopyPath, "party.db", CompressionLevel.Optimal);

        // Ключи обязательно входят в бэкап: без vapid.json перестанут работать push-подписки
        AddOptionalFile(archive, "vapid.json", _configuration["Push:KeysFile"] ?? "App_Data/vapid.json");
        AddOptionalFile(archive, "jwt.json", _configuration["Jwt:KeysFile"] ?? "App_Data/jwt.json");

        if (_configuration.GetValue("Backup:IncludeUploads", true))
            AddDirectory(archive, _configuration["Files:UploadRoot"] ?? "App_Data/uploads", "uploads");
    }

    private void AddOptionalFile(ZipArchive archive, string entryName, string path)
    {
        try
        {
            if (File.Exists(path))
                archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Файл {Path} не попал в бэкап", path);
        }
    }

    private void AddDirectory(ZipArchive archive, string directory, string entryPrefix)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, $"{entryPrefix}/{relative}", CompressionLevel.Optimal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Файл {Path} не попал в бэкап", file);
            }
        }
    }

    private void Rotate(string directory)
    {
        int keepCount = Math.Max(1, _configuration.GetValue("Backup:KeepCount", 5));

        List<FileInfo> files = new DirectoryInfo(directory)
            .GetFiles(ArchiveMask)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();

        foreach (FileInfo file in files.Skip(keepCount))
        {
            try
            {
                file.Delete();
                _logger.LogInformation("Старый бэкап удалён: {Name}", file.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить старый бэкап {Name}", file.Name);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Временный файл подчистит система — для бэкапа это не критично
        }
    }
}

/// <summary>Файл резервной копии для списка в админке.</summary>
public record BackupFile(string FileName, long SizeBytes, DateTime CreatedAtUtc);
