using System.Diagnostics;

namespace FactorioManager.Api;

public sealed record SystemHealthStatus(
    DateTimeOffset CheckedAt,
    long UptimeSeconds,
    long WorkingSetBytes,
    double CpuUsagePercent,
    long? DataFreeBytes,
    long? DataTotalBytes,
    int SaveCount,
    int BackupCount,
    int VersionCount,
    int ModFileCount,
    bool DatabasePresent);

public sealed class SystemHealthService(DataPaths paths)
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt = DateTimeOffset.UtcNow;

    public SystemHealthStatus GetSnapshot()
    {
        var process = Process.GetCurrentProcess();
        var now = DateTimeOffset.UtcNow;
        var cpu = process.TotalProcessorTime;
        var elapsed = now - _lastCpuAt;
        var cpuPercent = elapsed.TotalMilliseconds <= 0
            ? 0
            : Math.Clamp((cpu - _lastCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100, 0, 100);
        _lastCpu = cpu;
        _lastCpuAt = now;
        long? free = null;
        long? total = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(paths.Root))!);
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new(
            now,
            Math.Max(0, (long)(now - _startedAt).TotalSeconds),
            process.WorkingSet64,
            cpuPercent,
            free,
            total,
            CountFiles(paths.Saves, "*.zip"),
            CountFiles(paths.Backups, "*.zip"),
            CountDirectories(paths.Versions),
            CountFiles(paths.Mods, "*.zip"),
            File.Exists(paths.Database));
    }

    private static int CountFiles(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern).Count() : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static int CountDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Count() : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
