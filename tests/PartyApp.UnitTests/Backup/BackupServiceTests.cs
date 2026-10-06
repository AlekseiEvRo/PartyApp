using System.IO.Compression;

using FluentAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Modules.Backup;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Backup;

public class BackupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _uploads;
    private readonly string _backups;
    private readonly string _vapidFile;
    private readonly AppDbContext _db;
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "party-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        string databasePath = Path.Combine(_root, "party.db");
        _uploads = Path.Combine(_root, "uploads");
        _backups = Path.Combine(_root, "backups");
        _vapidFile = Path.Combine(_root, "vapid.json");

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _db.Users.Add(TestData.User("alice", UserRole.Player, "Алиса"));
        _db.SaveChanges();

        _service = new BackupService(
            _db,
            TestConfiguration.Create(
                ("Backup:Directory", _backups),
                ("Backup:KeepCount", "5"),
                ("Files:UploadRoot", _uploads),
                ("Push:KeysFile", _vapidFile),
                ("Jwt:KeysFile", Path.Combine(_root, "jwt.json"))),
            NullLogger<BackupService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CreateAsync_PacksDatabaseVapidKeyAndUploads()
    {
        Directory.CreateDirectory(Path.Combine(_uploads, "2026"));
        await File.WriteAllBytesAsync(Path.Combine(_uploads, "2026", "photo.jpg"), new byte[] { 1, 2, 3 });
        await File.WriteAllTextAsync(_vapidFile, "{\"PublicKey\":\"pub\"}");

        string archivePath = await _service.CreateAsync();

        File.Exists(archivePath).Should().BeTrue();

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        archive.Entries.Select(e => e.FullName).Should().Contain(
            new[] { "party.db", "vapid.json", "uploads/2026/photo.jpg" });

        string extracted = Path.Combine(_root, "extracted.db");
        archive.GetEntry("party.db")!.ExtractToFile(extracted, overwrite: true);

        using SqliteConnection connection = new($"Data Source={extracted}");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Users";

        Convert.ToInt64(command.ExecuteScalar()).Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WithoutOptionalFiles_StillCreatesArchive()
    {
        string archivePath = await _service.CreateAsync();

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        archive.Entries.Select(e => e.FullName).Should().Equal("party.db");
    }

    [Fact]
    public async Task CreateAsync_RotatesOldBackupsKeepingConfiguredCount()
    {
        Directory.CreateDirectory(_backups);
        for (int day = 1; day <= 7; day++)
            await File.WriteAllTextAsync(Path.Combine(_backups, $"party-backup-2026010{day}-000000-000.zip"), "old");

        string created = await _service.CreateAsync();

        List<string> files = Directory.GetFiles(_backups, "party-backup-*.zip")
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        files.Should().HaveCount(5);
        files.Should().Contain(Path.GetFileName(created));
        files.Should().NotContain("party-backup-20260101-000000-000.zip");
        files.Should().NotContain("party-backup-20260103-000000-000.zip");
    }

    [Fact]
    public async Task List_ReturnsCreatedBackupsNewestFirst()
    {
        string first = await _service.CreateAsync();
        await Task.Delay(20);
        string second = await _service.CreateAsync();

        IReadOnlyList<BackupFile> files = _service.List();

        files.Should().HaveCount(2);
        files[0].FileName.Should().Be(Path.GetFileName(second));
        files[1].FileName.Should().Be(Path.GetFileName(first));
        files[0].SizeBytes.Should().BeGreaterThan(0);
    }
}
