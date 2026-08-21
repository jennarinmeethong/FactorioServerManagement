namespace FactorioManager.Api;

public sealed record MaintenanceHistoryEntry(
    string Operation,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Message);

public sealed class MaintenanceHistoryService(StateStore state)
{
    private const string StateKey = "maintenance_history";
    private const int MaxEntries = 50;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<MaintenanceHistoryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = await state.GetAsync<List<MaintenanceHistoryEntry>>(StateKey, cancellationToken) ?? [];
        return entries;
    }

    public async Task RecordAsync(
        string operation,
        bool success,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var entry = new MaintenanceHistoryEntry(
            operation,
            success ? "success" : "failure",
            startedAt,
            completedAt,
            SafeMessage(message, success));

        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await state.GetAsync<List<MaintenanceHistoryEntry>>(StateKey, cancellationToken) ?? [];
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            await state.SetAsync(StateKey, entries, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string SafeMessage(string? message, bool success)
    {
        var value = string.IsNullOrWhiteSpace(message)
            ? (success ? "Completed successfully." : "Operation failed.")
            : message.Trim();
        value = System.Text.RegularExpressions.Regex.Replace(value, "(?i)(password|token|authorization|secret)\\s*[:=]\\s*\\S+", "$1=[redacted]");
        return value.Length <= 512 ? value : value[..512];
    }
}
