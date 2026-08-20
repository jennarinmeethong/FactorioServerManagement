namespace FactorioManager.Api;

public sealed class MaintenanceWorker(
    IServiceProvider services,
    ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var state = scope.ServiceProvider.GetRequiredService<StateStore>();
                var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
                var versions = scope.ServiceProvider.GetRequiredService<VersionService>();
                var supervisor = scope.ServiceProvider.GetRequiredService<ServerSupervisor>();
                var settings = await state.GetAsync<ServerSettings>("settings", stoppingToken) ?? new ServerSettings();
                var lastBackup = await state.GetAsync<DateTimeOffset?>("last_scheduled_backup", stoppingToken);
                if (!supervisor.IsRunning && !string.IsNullOrWhiteSpace(settings.ActiveSave) && (lastBackup is null || DateTimeOffset.UtcNow - lastBackup >= TimeSpan.FromHours(settings.BackupIntervalHours)))
                {
                    await backup.CreateBackupAsync("scheduled", stoppingToken);
                    await state.SetAsync<DateTimeOffset?>("last_scheduled_backup", DateTimeOffset.UtcNow, stoppingToken);
                }
                var lastUpdateCheck = await state.GetAsync<DateTimeOffset?>("last_update_check", stoppingToken);
                if (!string.IsNullOrWhiteSpace(settings.ActiveVersion) && (lastUpdateCheck is null || DateTimeOffset.UtcNow - lastUpdateCheck >= TimeSpan.FromHours(1)))
                {
                    await versions.CheckForUpdateAsync(stoppingToken);
                    await state.SetAsync<DateTimeOffset?>("last_update_check", DateTimeOffset.UtcNow, stoppingToken);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Scheduled Factorio maintenance failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
