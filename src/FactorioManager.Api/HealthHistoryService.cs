namespace FactorioManager.Api;

public sealed class HealthHistoryService(StateStore state)
{
    private const string StateKey = "health_history";
    private const int MaxEntries = 240;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<HealthSample>> ListAsync(CancellationToken cancellationToken = default)
        => await state.GetAsync<List<HealthSample>>(StateKey, cancellationToken) ?? [];

    public async Task RecordAsync(SystemHealthStatus snapshot, CancellationToken cancellationToken = default)
    {
        var entry = new HealthSample(
            snapshot.CheckedAt,
            snapshot.CpuUsagePercent,
            snapshot.WorkingSetBytes,
            snapshot.DataFreeBytes,
            snapshot.SaveCount,
            snapshot.BackupCount,
            snapshot.VersionCount,
            snapshot.ModFileCount);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await state.GetAsync<List<HealthSample>>(StateKey, cancellationToken) ?? [];
            entries.Add(entry);
            if (entries.Count > MaxEntries)
                entries.RemoveRange(0, entries.Count - MaxEntries);
            await state.SetAsync(StateKey, entries, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
