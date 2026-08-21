namespace FactorioManager.Api;

public sealed record ServerEventHistoryEntry(
    string EventKind,
    string Status,
    DateTimeOffset Timestamp,
    string Message);

public sealed class ServerEventHistoryService(StateStore state)
{
    private const string StateKey = "server_event_history";
    private const int MaxEntries = 50;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<ServerEventHistoryEntry>> ListAsync(CancellationToken cancellationToken = default)
        => await state.GetAsync<List<ServerEventHistoryEntry>>(StateKey, cancellationToken) ?? [];

    public async Task RecordAsync(
        string eventKind,
        string status,
        DateTimeOffset timestamp,
        string? message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventKind)) throw new ArgumentException("Event kind is required.", nameof(eventKind));
        if (string.IsNullOrWhiteSpace(status)) throw new ArgumentException("Status is required.", nameof(status));

        var entry = new ServerEventHistoryEntry(
            eventKind.Trim(),
            status.Trim(),
            timestamp,
            SafeMessage(message));

        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await state.GetAsync<List<ServerEventHistoryEntry>>(StateKey, cancellationToken) ?? [];
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

    private static string SafeMessage(string? message)
    {
        var value = string.IsNullOrWhiteSpace(message) ? "No additional details." : message.Trim();
        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            "(?i)(password|token|authorization|secret)\\s*[:=]\\s*\\S+",
            "$1=[redacted]");
        return value.Length <= 512 ? value : value[..512];
    }
}
