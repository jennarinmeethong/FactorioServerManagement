using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace FactorioManager.Api;

public sealed class StatusHub : Hub;

public sealed class ServerSupervisor(
    DataPaths paths,
    StateStore store,
    IHubContext<StatusHub> hub,
    ILogger<ServerSupervisor> logger,
    ServerEventHistoryService? eventHistory = null,
    NotificationService? notifications = null,
    MapControlCatalogService? catalogs = null)
{
    private readonly MapControlCatalogService? _catalogs = catalogs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _recentLogs = new();
    private readonly ConcurrentDictionary<Process, bool> _manualStopRequests = new();
    private Process? _process;
    private ServerStatus _status = new(ServerState.Stopped, null, DateTimeOffset.UtcNow, 0, null);
    public RconEndpoint? RconEndpoint { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public Task<ServerStatus> GetStatusAsync() => Task.FromResult(_status);

    public async Task<ServerStatus> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Never retain an ephemeral credential across a failed/retried startup.
            RconEndpoint = null;
            if (_process is { HasExited: false }) return _status;
            var settings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
            if (string.IsNullOrWhiteSpace(settings.ActiveVersion)) throw new InvalidOperationException("Choose and download a Factorio version before starting the server.");
            if (string.IsNullOrWhiteSpace(settings.ActiveSave)) throw new InvalidOperationException("Choose or upload a save before starting the server.");
            var executable = GetExecutable(settings.ActiveVersion);
            var save = Path.Combine(paths.Saves, Path.GetFileName(settings.ActiveSave));
            if (!File.Exists(executable)) throw new InvalidOperationException($"Factorio executable was not found for version {settings.ActiveVersion}.");
            if (!File.Exists(save)) throw new InvalidOperationException("The selected save no longer exists.");

            await WriteExpansionModListAsync(settings, cancellationToken);
            await WriteServerSettingsAsync(settings, cancellationToken);
            await SetStatusAsync(new(ServerState.Starting, null, DateTimeOffset.UtcNow, _status.RestartAttempt, null));
            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--start-server");
            startInfo.ArgumentList.Add(save);
            startInfo.ArgumentList.Add("--server-settings");
            startInfo.ArgumentList.Add(Path.Combine(paths.Config, "server-settings.json"));
            // The RCON listener is process-local and never exposed by Docker/network configuration.
            var rconPort = GetFreeLoopbackPort();
            var rconPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            RconEndpoint = new RconEndpoint(rconPort, rconPassword);
            startInfo.ArgumentList.Add("--rcon-port"); startInfo.ArgumentList.Add(rconPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--rcon-password"); startInfo.ArgumentList.Add(rconPassword);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _ = WriteLogAsync(eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _ = WriteLogAsync(eventArgs.Data); };
            process.Exited += (_, _) => _ = OnExitedAsync(process);
            _manualStopRequests[process] = false;
            if (!process.Start()) throw new InvalidOperationException("Factorio process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            await SetStatusAsync(new(ServerState.Running, process.Id, DateTimeOffset.UtcNow, 0, null));
            return _status;
        }
        catch
        {
            RconEndpoint = null;
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<ServerStatus> StopAsync(CancellationToken cancellationToken)
    {
        // Publish the manual-stop intent before waiting for the supervisor gate. The
        // process exit callback can run concurrently with this method.
        MarkManualStop(_process);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is null || _process.HasExited)
            {
                RconEndpoint = null;
                await SetStatusAsync(new(ServerState.Stopped, null, DateTimeOffset.UtcNow, 0, null));
                return _status;
            }
            var process = _process;
            MarkManualStop(process);
            await SetStatusAsync(new(ServerState.Stopping, process.Id, DateTimeOffset.UtcNow, 0, null));
            await process.StandardInput.WriteLineAsync("/quit");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Factorio did not stop cleanly within 30 seconds; terminating process {ProcessId}", process.Id);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            _process = null;
            RconEndpoint = null;
            await SetStatusAsync(new(ServerState.Stopped, null, DateTimeOffset.UtcNow, 0, null));
            return _status;
        }
        finally { _gate.Release(); }
    }

    public async Task<ServerStatus> RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        return await StartAsync(cancellationToken);
    }

    public async Task<string> CreateSaveAsync(string name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false }) throw new InvalidOperationException("Stop the server before creating a new map.");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("Choose a valid save name.");
            var settings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
            if (string.IsNullOrWhiteSpace(settings.ActiveVersion)) throw new InvalidOperationException("Choose and download a Factorio version before creating a map.");
            var executable = GetExecutable(settings.ActiveVersion);
            if (!File.Exists(executable)) throw new InvalidOperationException("The selected Factorio executable is unavailable.");
            var saveName = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.zip";
            var savePath = Path.Combine(paths.Saves, saveName);
            if (File.Exists(savePath)) throw new InvalidOperationException("A save with that name already exists.");
            await WriteExpansionModListAsync(settings, cancellationToken);
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("--create");
            info.ArgumentList.Add(savePath);
            var mapSettingsPath = Path.Combine(paths.Config, "map-gen-settings.json");
            await WriteMapGenerationSettingsAsync(settings.MapGeneration, mapSettingsPath, cancellationToken);
            info.ArgumentList.Add("--map-gen-settings");
            info.ArgumentList.Add(mapSettingsPath);
            var runtimeMapSettingsPath = Path.Combine(paths.Config, "map-settings.json");
            await File.WriteAllTextAsync(runtimeMapSettingsPath, MapGenerationSettingsJson.SerializeMapSettings(settings.MapGeneration), cancellationToken);
            info.ArgumentList.Add("--map-settings");
            info.ArgumentList.Add(runtimeMapSettingsPath);
            var started = Stopwatch.GetTimestamp();
            logger.LogInformation("Save create started for {SaveName}", saveName);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Factorio could not create the new map.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var diagnostics = SafeDiagnostics.Redact(string.Join(Environment.NewLine, new[] { stderr, stdout }.Where(text => !string.IsNullOrWhiteSpace(text))));
                logger.LogInformation("Save create exited for {SaveName}: code={ExitCode}, durationMs={DurationMs}, result={Result}", saveName, process.ExitCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, process.ExitCode == 0 ? "success" : "failure");
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Factorio could not create the save (exit code {process.ExitCode}).{(string.IsNullOrWhiteSpace(diagnostics) ? " Check the server logs for details." : $" Details: {diagnostics}")}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                logger.LogWarning("Save create timed out for {SaveName} after {DurationMs}ms", saveName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw new InvalidOperationException("Save creation timed out after 2 minutes. Check the server logs and try again.");
            }
            if (!File.Exists(savePath))
                throw new InvalidOperationException("Factorio reported success, but the generated save file was not found. The active save was not changed.");
            await store.SetAsync("settings", settings with { ActiveSave = saveName }, cancellationToken);
            return saveName;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ReadLogsAsync(CancellationToken cancellationToken)
    {
        var logPath = Path.Combine(paths.Logs, "factorio.log");
        if (File.Exists(logPath))
        {
            var lines = (await File.ReadAllLinesAsync(logPath, cancellationToken)).TakeLast(500);
            _recentLogs.Clear();
            foreach (var line in lines)
            {
                var safe = SafeDiagnostics.Redact(line);
                _recentLogs.Enqueue(safe);
            }
            while (_recentLogs.Count > 500) _recentLogs.TryDequeue(out _);
        }
        return _recentLogs.ToArray();
    }

    private async Task OnExitedAsync(Process exited)
    {
        var wasManual = _manualStopRequests.TryRemove(exited, out var manualStopRequested) && manualStopRequested;
        var exitCode = exited.ExitCode;
        var wasCurrent = ReferenceEquals(_process, exited);
        if (wasCurrent) { _process = null; RconEndpoint = null; }
        if (!ShouldAutoRestartAfterExit(wasManual))
        {
            await SetStatusAsync(new(ServerState.Stopped, null, DateTimeOffset.UtcNow, 0, null));
            return;
        }
        var attempt = Math.Min(_status.RestartAttempt + 1, 6);
        var exitMessage = $"Factorio exited unexpectedly with code {exitCode}.";
        if (eventHistory is not null)
            await eventHistory.RecordAsync("unexpected-exit", "failure", DateTimeOffset.UtcNow, exitMessage);
        if (notifications is not null)
            await notifications.SendAsync("Factorio exited unexpectedly", exitMessage);
        await SetStatusAsync(new(ServerState.Failed, null, DateTimeOffset.UtcNow, attempt, exitMessage));
        var delay = TimeSpan.FromSeconds(Math.Min(60, 2 << attempt));
        logger.LogWarning("Factorio exited with code {ExitCode}; restarting in {Delay}", exitCode, delay);
        await Task.Delay(delay);
        try
        {
            await StartAsync(CancellationToken.None);
            if (eventHistory is not null)
                await eventHistory.RecordAsync("automatic-restart", "success", DateTimeOffset.UtcNow, $"Automatic restart attempt {attempt} succeeded.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatic Factorio restart failed");
            await SetStatusAsync(new(ServerState.Failed, null, DateTimeOffset.UtcNow, attempt, exception.Message));
            if (eventHistory is not null)
                await eventHistory.RecordAsync("automatic-restart", "failure", DateTimeOffset.UtcNow, exception.Message);
        }
    }

    internal static bool ShouldAutoRestartAfterExit(bool manualStopRequested) => !manualStopRequested;

    private void MarkManualStop(Process? process)
    {
        if (process is not null)
            _manualStopRequests[process] = true;
    }

    private async Task WriteServerSettingsAsync(ServerSettings settings, CancellationToken cancellationToken)
    {
        var document = new
        {
            name = settings.ServerName,
            description = settings.Description,
            max_players = settings.MaxPlayers,
            tags = settings.Tags,
            visibility = new { @public = settings.VisibilityPublic, lan = settings.VisibilityLan },
            username = "",
            password = settings.ServerPassword ?? "",
            autosave_interval = settings.AutosaveMinutes,
            autosave_slots = settings.AutosaveSlots,
            ignore_player_limit_for_returning_players = settings.IgnorePlayerLimitForReturningPlayers,
            allow_commands = settings.AllowCommands
        };
        await using var file = File.Create(Path.Combine(paths.Config, "server-settings.json"));
        await JsonSerializer.SerializeAsync(file, document, cancellationToken: cancellationToken);
    }

    private async Task WriteExpansionModListAsync(ServerSettings settings, CancellationToken cancellationToken)
    {
        var installed = await store.GetAsync<ModEntry[]>("mods", cancellationToken) ?? [];
        var expansionEnabled = string.Equals(settings.Expansion, "space-age", StringComparison.OrdinalIgnoreCase);
        var entries = new List<object>
        {
            new { name = "base", enabled = true },
            new { name = "elevated-rails", enabled = expansionEnabled },
            new { name = "quality", enabled = expansionEnabled },
            new { name = "space-age", enabled = expansionEnabled }
        };
        entries.AddRange(installed
            .Where(mod => !mod.Name.Equals("base", StringComparison.OrdinalIgnoreCase)
                && !mod.Name.Equals("elevated-rails", StringComparison.OrdinalIgnoreCase)
                && !mod.Name.Equals("quality", StringComparison.OrdinalIgnoreCase)
                && !mod.Name.Equals("space-age", StringComparison.OrdinalIgnoreCase))
            .Select(mod => (object)new { name = mod.Name, enabled = mod.Enabled }));
        await File.WriteAllTextAsync(Path.Combine(paths.Mods, "mod-list.json"), JsonSerializer.Serialize(new { mods = entries }), cancellationToken);
    }

    private async Task WriteMapGenerationSettingsAsync(MapGenerationSettings source, string path, CancellationToken cancellationToken)
    {
        if (_catalogs is null) throw new InvalidOperationException("Map-control catalog service is unavailable.");
        var settings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        var catalog = await _catalogs.ResolveForSettingsAsync(settings, cancellationToken);
        await File.WriteAllTextAsync(path, MapGenerationSettingsJson.SerializeMapGeneration(MapControlCatalogService.Normalize(source, catalog), catalog), cancellationToken);
    }

    private string GetExecutable(string version) => Path.Combine(paths.Versions, version, "bin", "x64", "factorio");

    private static int GetFreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private async Task WriteLogAsync(string line)
    {
        // Secrets are never expected in Factorio output; this removes the two known credential labels defensively.
        line = SafeDiagnostics.Redact(line);
        var timestamped = $"{DateTimeOffset.UtcNow:O} {line}";
        _recentLogs.Enqueue(timestamped);
        while (_recentLogs.Count > 500) _recentLogs.TryDequeue(out _);
        var logPath = Path.Combine(paths.Logs, "factorio.log");
        if (File.Exists(logPath) && new FileInfo(logPath).Length > 10 * 1024 * 1024)
        {
            var archivePath = Path.Combine(paths.Logs, "factorio.log.1");
            File.Move(logPath, archivePath, overwrite: true);
        }
        await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        await hub.Clients.All.SendAsync("log", timestamped);
    }

    private async Task SetStatusAsync(ServerStatus status)
    {
        _status = status;
        await hub.Clients.All.SendAsync("status", status);
    }
}
