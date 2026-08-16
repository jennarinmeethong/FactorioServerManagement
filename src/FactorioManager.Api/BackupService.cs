namespace FactorioManager.Api;

public sealed class BackupService(DataPaths paths, StateStore store)
{
    public async Task<string> CreateBackupAsync(string reason, CancellationToken cancellationToken = default)
    {
        var settings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        if (string.IsNullOrWhiteSpace(settings.ActiveSave)) throw new InvalidOperationException("Select an active save before creating a backup.");
        var source = Path.Combine(paths.Saves, Path.GetFileName(settings.ActiveSave));
        if (!File.Exists(source)) throw new InvalidOperationException("The selected save does not exist.");
        var name = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{reason}-{Path.GetFileName(source)}";
        await using var input = File.OpenRead(source);
        await using var output = File.Create(Path.Combine(paths.Backups, name));
        await input.CopyToAsync(output, cancellationToken);
        await PruneAsync(settings.BackupRetention, cancellationToken);
        return name;
    }

    public async Task RestoreAsync(string backupName, CancellationToken cancellationToken = default)
    {
        var source = Path.Combine(paths.Backups, backupName);
        if (!File.Exists(source) || !backupName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The requested backup was not found.");
        var targetName = backupName[(backupName.IndexOf('-', backupName.IndexOf('-', StringComparison.Ordinal) + 1) + 1)..];
        if (string.IsNullOrWhiteSpace(targetName) || targetName.Contains(Path.DirectorySeparatorChar)) throw new InvalidOperationException("The backup name is invalid.");
        await using var input = File.OpenRead(source);
        await using var output = File.Create(Path.Combine(paths.Saves, targetName));
        await input.CopyToAsync(output, cancellationToken);
        var settings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        await store.SetAsync("settings", settings with { ActiveSave = targetName }, cancellationToken);
    }

    private async Task PruneAsync(int retention, CancellationToken cancellationToken)
    {
        var old = Directory.EnumerateFiles(paths.Backups, "*.zip").OrderByDescending(File.GetCreationTimeUtc).Skip(retention).ToArray();
        foreach (var file in old) { File.Delete(file); await Task.Yield(); }
    }
}
