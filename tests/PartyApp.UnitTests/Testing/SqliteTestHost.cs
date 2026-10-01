using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using PartyApp.Infrastructure.Persistence;

namespace PartyApp.UnitTests.Testing;

/// <summary>
/// Реальный DI-контейнер поверх SQLite in-memory.
/// Создаёт схему через EnsureCreated и отдаёт тот же IServiceScopeFactory,
/// который singleton-сервисы приложения используют в бою.
/// </summary>
public sealed class SqliteTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private IServiceScope? _sharedScope;

    public SqliteTestHost(Action<IServiceCollection>? configureServices = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

        configureServices?.Invoke(services);

        _provider = services.BuildServiceProvider();

        using IServiceScope scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public ServiceProvider Services => _provider;

    /// <summary>
    /// Общий на весь тест AppDbContext (один scope). Удобен, когда сервис принимает
    /// scoped-контекст напрямую, как AuthService.
    /// </summary>
    public AppDbContext Db
    {
        get
        {
            _sharedScope ??= _provider.CreateScope();
            return _sharedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        }
    }

    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>Выполняет действие в новом scope с AppDbContext.</summary>
    public async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using IServiceScope scope = _provider.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    /// <summary>Выполняет действие в новом scope с AppDbContext.</summary>
    public Task DbAsync(Func<AppDbContext, Task> action)
    {
        return DbAsync<object?>(async db =>
        {
            await action(db);
            return null;
        });
    }

    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

    public void Dispose()
    {
        _sharedScope?.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }
}