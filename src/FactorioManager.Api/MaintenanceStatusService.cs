namespace FactorioManager.Api;

public sealed record MaintenanceStatus(
    DateTimeOffset CheckedAt,
    int BackupIntervalHours,
    int BackupRetention,
    DateTimeOffset? LastScheduledBackup,
    DateTimeOffset? NextScheduledBackup,
    DateTimeOffset? LastUpdateCheck,
    DateTimeOffset? NextUpdateCheck);

public sealed class MaintenanceStatusService(StateStore state)
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
            lastBackup,
            lastBackup?.AddHours(settings.BackupIntervalHours),
            lastUpdate,
            lastUpdate?.AddHours(1));
    }
}
