using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PartyApp.Api.Modules.Push;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.IntegrationTests.Infrastructure;

/// <summary>
/// Поднимает настоящее приложение с изолированной SQLite-БД в Temp.
/// На старте применяются реальные миграции и сид определений ивентов,
/// как в проде; push-сервис заменяется на записывающий фейк.
/// </summary>
public class PartyAppFactory : WebApplicationFactory<Program>
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly string _dictionaryPath;

    public PartyAppFactory()
    {
        // Ключ должен быть виден ещё до старта хоста, иначе JwtSigningKeyStore
        // успеет сгенерировать файл в тестовом каталоге.
        Environment.SetEnvironmentVariable("Jwt__SigningKey", TestJwt.SigningKey);

        _directory = Path.Combine(Path.GetTempPath(), "party-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _databasePath = Path.Combine(_directory, "party.db");
        _dictionaryPath = Path.Combine(_directory, "russian.txt");

        File.WriteAllLines(
            _dictionaryPath,
            new[] { "тарелка", "апельсин", "сеанс", "торт", "ежик", "ёжик", "карамель" },
            Encoding.UTF8);
    }

    public FakePushNotificationService Push { get; } = new();

    public string DatabasePath => _databasePath;

    /// <summary>
    /// Дополнительные настройки конфигурации для конкретного теста
    /// (например, включение rate limiting с маленьким лимитом).
    /// Задавать до первого CreateClient — конфигурация читается при старте приложения.
    /// </summary>
    public Action<Dictionary<string, string?>>? ConfigureOverrides { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            Dictionary<string, string?> settings = new()
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={_databasePath};Cache=Shared;Foreign Keys=True",
                ["Jwt:SigningKey"] = TestJwt.SigningKey,
                ["Jwt:Issuer"] = TestJwt.Issuer,
                ["Jwt:Audience"] = TestJwt.Audience,
                ["Party:WelcomeBonus"] = "100",
                ["Dictionary:FilePath"] = _dictionaryPath,
                ["Push:KeysFile"] = Path.Combine(_directory, "vapid.json"),
                ["Jwt:KeysFile"] = Path.Combine(_directory, "jwt.json"),
                ["Files:UploadRoot"] = Path.Combine(_directory, "uploads"),
                ["Wishes:RequireModeration"] = "true",
                ["Toast:CooldownSeconds"] = "30",
                ["Toast:Points"] = "1",
                ["RateLimiting:Enabled"] = "false",
                ["Backup:Enabled"] = "false",
                ["Serilog:LogFile"] = Path.Combine(_directory, "logs", "party-.log"),
                ["Serilog:MinimumLevel:Default"] = "Warning"
            };

            ConfigureOverrides?.Invoke(settings);
            configuration.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPushNotificationService>();
            services.AddSingleton<IPushNotificationService>(Push);
        });
    }

    public async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using IServiceScope scope = Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    public Task DbAsync(Func<AppDbContext, Task> action)
    {
        return DbAsync<object?>(async db =>
        {
            await action(db);
            return null;
        });
    }

    /// <summary>Добавляет определение ивента в БД тестового приложения.</summary>
    public async Task<EventDefinition> SeedDefinitionAsync(
        string type,
        string configJson = "{}",
        bool isActive = true,
        string? displayName = null)
    {
        EventDefinition definition = new()
        {
            Type = type,
            DisplayName = displayName ?? $"Event {type}",
            ConfigJson = configJson,
            IsActive = isActive,
            CreatedById = null
        };

        await DbAsync(async db =>
        {
            db.EventDefinitions.Add(definition);
            await db.SaveChangesAsync();
        });

        return definition;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Файлы БД могут ещё освобождаться — для тестов это не критично.
            }
        }
    }
}