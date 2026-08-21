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
                var history = scope.ServiceProvider.GetRequiredService<MaintenanceHistoryService>();
                var supervisor = scope.ServiceProvider.GetRequiredService<ServerSupervisor>();
                var health = scope.ServiceProvider.GetRequiredService<SystemHealthService>();
                var healthHistory = scope.ServiceProvider.GetRequiredService<HealthHistoryService>();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
                var settings = await state.GetAsync<ServerSettings>("settings", stoppingToken) ?? new ServerSettings();
                var now = DateTimeOffset.UtcNow;
                var lastScheduledRestartDate = await state.GetAsync<string>("last_scheduled_restart_date", stoppingToken);
                var lastBackup = await state.GetAsync<DateTimeOffset?>("last_scheduled_backup", stoppingToken);
                if (!supervisor.IsRunning && !string.IsNullOrWhiteSpace(settings.ActiveSave) && (lastBackup is null || now - lastBackup >= TimeSpan.FromHours(settings.BackupIntervalHours)))
                {
                    var started = now;
                    try
                    {
                        await backup.CreateBackupAsync("scheduled", stoppingToken);
                        await state.SetAsync<DateTimeOffset?>("last_scheduled_backup", DateTimeOffset.UtcNow, stoppingToken);
                        await history.RecordAsync("scheduled-backup", true, started, DateTimeOffset.UtcNow, "Scheduled backup completed.", stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        await history.RecordAsync("scheduled-backup", false, started, DateTimeOffset.UtcNow, exception.Message, stoppingToken);
                        await notifications.SendAsync("Scheduled backup failed", exception.Message, stoppingToken);
                        logger.LogError(exception, "Scheduled Factorio backup failed");
                    }
                }
                var lastUpdateCheck = await state.GetAsync<DateTimeOffset?>("last_update_check", stoppingToken);
                if (settings.ScheduledUpdateChecksEnabled && !string.IsNullOrWhiteSpace(settings.ActiveVersion) && (lastUpdateCheck is null || now - lastUpdateCheck >= TimeSpan.FromHours(1)))
                {
                    var started = DateTimeOffset.UtcNow;
                    try
                    {
                        var result = await versions.CheckForUpdateAsync(stoppingToken);
                        var success = string.IsNullOrWhiteSpace(result.Error);
                        if (success)
                            await state.SetAsync<DateTimeOffset?>("last_update_check", DateTimeOffset.UtcNow, stoppingToken);
                        await history.RecordAsync("scheduled-update-check", success, started, DateTimeOffset.UtcNow, success ? "Scheduled update check completed." : result.Error, stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        await history.RecordAsync("scheduled-update-check", false, started, DateTimeOffset.UtcNow, exception.Message, stoppingToken);
                        await notifications.SendAsync("Scheduled update check failed", exception.Message, stoppingToken);
                        logger.LogError(exception, "Scheduled Factorio update check failed");
                    }
                }

                if (supervisor.IsRunning && IsRestartDue(settings, now, lastScheduledRestartDate))
                {
                    var localDate = GetLocalDate(settings, now);
                    await state.SetAsync("last_scheduled_restart_date", localDate, stoppingToken);
                    var started = now;
                    try
                    {
                        await supervisor.RestartAsync(stoppingToken);
                        await history.RecordAsync("scheduled-restart", true, started, DateTimeOffset.UtcNow, "Scheduled restart completed.", stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        await history.RecordAsync("scheduled-restart", false, started, DateTimeOffset.UtcNow, exception.Message, stoppingToken);
                        await notifications.SendAsync("Scheduled restart failed", exception.Message, stoppingToken);
                        logger.LogError(exception, "Scheduled Factorio restart failed");
                    }
                }

                await healthHistory.RecordAsync(health.GetSnapshot(), stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Scheduled Factorio maintenance failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private static bool IsRestartDue(ServerSettings settings, DateTimeOffset now, string? lastScheduledRestartDate)
    {
        if (!settings.ScheduledRestartEnabled || !TimeOnly.TryParseExact(settings.ScheduledRestartTime, "HH:mm", out var target))
            return false;
        var local = GetLocalTime(settings, now);
        return local.Hour == target.Hour && local.Minute == target.Minute &&
            !string.Equals(GetLocalDate(settings, now), lastScheduledRestartDate, StringComparison.Ordinal);
    }

    private static string GetLocalDate(ServerSettings settings, DateTimeOffset now)
        => GetLocalTime(settings, now).ToString("yyyy-MM-dd");

    private static DateTimeOffset GetLocalTime(ServerSettings settings, DateTimeOffset now)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
            return TimeZoneInfo.ConvertTime(now, zone);
        }
        catch (TimeZoneNotFoundException)
        {
            return now;
        }
        catch (InvalidTimeZoneException)
        {
            return now;
        }
    }
}
