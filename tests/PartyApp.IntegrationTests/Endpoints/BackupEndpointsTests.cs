using System.IO.Compression;
using System.Net;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class BackupEndpointsTests
{
    [Fact]
    public async Task Backup_AsAdmin_ReturnsZipWithDatabaseAndListsIt()
    {
        // Путь относительный специально: раньше Results.File искал такой архив в wwwroot и падал с 500
        string backupDirectory = Path.Combine("App_Data", "test-backups-" + Guid.NewGuid().ToString("N"));

        try
        {
            using PartyAppFactory factory = new()
            {
                ConfigureOverrides = settings =>
                {
                    settings["Backup:Directory"] = backupDirectory;
                    settings["Backup:OnStartup"] = "false";
                }
            };

            PartyAppApi api = new(factory);
            TestUser admin = await api.CreateAdminAsync();
            api.Authorize(admin);

            HttpResponseMessage response = await api.Client.GetAsync("/api/admin/backup");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
            response.Content.Headers.ContentDisposition!.FileName.Should().Contain("party-backup-");

            await using Stream content = await response.Content.ReadAsStreamAsync();
            using ZipArchive archive = new(content, ZipArchiveMode.Read);
            archive.Entries.Select(e => e.FullName).Should().Contain("party.db");

            HttpResponseMessage list = await api.Client.GetAsync("/api/admin/backups");
            list.StatusCode.Should().Be(HttpStatusCode.OK);
            (await PartyAppApi.ReadJsonAsync(list)).GetArrayLength().Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(backupDirectory))
                Directory.Delete(backupDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BackupOnStartup_CreatesArchive()
    {
        string backupDirectory = Path.Combine(Path.GetTempPath(), "party-backups-" + Guid.NewGuid().ToString("N"));

        try
        {
            using PartyAppFactory factory = new()
            {
                ConfigureOverrides = settings =>
                {
                    settings["Backup:Enabled"] = "true";
                    settings["Backup:OnStartup"] = "true";
                    settings["Backup:Directory"] = backupDirectory;
                }
            };

            using HttpClient client = factory.CreateClient();

            string? archive = null;
            for (int attempt = 0; attempt < 50 && archive is null; attempt++)
            {
                await Task.Delay(100);
                if (Directory.Exists(backupDirectory))
                    archive = Directory.GetFiles(backupDirectory, "*.zip").FirstOrDefault();
            }

            archive.Should().NotBeNull("автобэкап должен создаваться на старте");
        }
        finally
        {
            if (Directory.Exists(backupDirectory))
                Directory.Delete(backupDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_AsPlayer_ReturnsForbidden()
    {
        using PartyAppFactory factory = new();
        PartyAppApi api = new(factory);
        TestUser player = await api.RegisterAsync();
        api.Authorize(player);

        HttpResponseMessage response = await api.Client.GetAsync("/api/admin/backup");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
