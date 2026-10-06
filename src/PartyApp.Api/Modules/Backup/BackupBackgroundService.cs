namespace PartyApp.Api.Modules.Backup;

/// <summary>
/// Делает автобэкап на старте и затем раз в Backup:IntervalHours.
/// Выключен при Backup:Enabled=false (например, в интеграционных тестах).
/// </summary>
public class BackupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackupBackgroundService> _logger;

    public BackupBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<BackupBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Backup:Enabled", true))
        {
            _logger.LogInformation("Автобэкапы выключены (Backup:Enabled=false)");
            return;
        }

        if (_configuration.GetValue("Backup:OnStartup", true))
            await RunOnceAsync(stoppingToken);

        double intervalHours = Math.Max(1, _configuration.GetValue("Backup:IntervalHours", 24));

        using PeriodicTimer timer = new(TimeSpan.FromHours(intervalHours), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            BackupService backups = scope.ServiceProvider.GetRequiredService<BackupService>();
            await backups.CreateAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось создать автобэкап");
        }
    }
}
