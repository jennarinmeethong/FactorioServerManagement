namespace FactorioManager.Api;

public sealed record MaintenanceStatus(
    DateTimeOffset CheckedAt,
    int BackupIntervalHours,
    int BackupRetention,
    bool ScheduledRestartEnabled,
    string ScheduledRestartTime,
    DateTimeOffset? LastScheduledBackup,
    DateTimeOffset? NextScheduledBackup,
    DateTimeOffset? LastUpdateCheck,
    DateTimeOffset? NextUpdateCheck,
    IReadOnlyList<MaintenanceHistoryEntry> RecentHistory);

public sealed class MaintenanceStatusService(StateStore state, MaintenanceHistoryService history)
{
    public async Task<MaintenanceStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await state.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        var lastBackup = await state.GetAsync<DateTimeOffset?>("last_scheduled_backup", cancellationToken);
        var lastUpdate = await state.GetAsync<DateTimeOffset?>("last_update_check", cancellationToken);
        return new(
            DateTimeOffset.UtcNow,
            settings.BackupIntervalHours,
            settings.BackupRetention,
            settings.ScheduledRestartEnabled,
            settings.ScheduledRestartTime,
            lastBackup,
            lastBackup?.AddHours(settings.BackupIntervalHours),
            lastUpdate,
            lastUpdate?.AddHours(1),
            await history.ListAsync(cancellationToken));
    }
}
