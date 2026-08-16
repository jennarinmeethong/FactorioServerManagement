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
    ILogger<ServerSupervisor> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _recentLogs = new();
    private Process? _process;
    private bool _manualStop;
    private ServerStatus _status = new(ServerState.Stopped, null, DateTimeOffset.UtcNow, 0, null);

    public Task<ServerStatus> GetStatusAsync() => Task.FromResult(_status);

    public async Task<ServerStatus> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false }) return _status;
            var settings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
            if (string.IsNullOrWhiteSpace(settings.ActiveVersion)) throw new InvalidOperationException("Choose and download a Factorio version before starting the server.");
            if (string.IsNullOrWhiteSpace(settings.ActiveSave)) throw new InvalidOperationException("Choose or upload a save before starting the server.");
            var executable = GetExecutable(settings.ActiveVersion);
            var save = Path.Combine(paths.Saves, Path.GetFileName(settings.ActiveSave));
            if (!File.Exists(executable)) throw new InvalidOperationException($"Factorio executable was not found for version {settings.ActiveVersion}.");
            if (!File.Exists(save)) throw new InvalidOperationException("The selected save no longer exists.");

            await WriteServerSettingsAsync(settings, cancellationToken);
            _manualStop = false;
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
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _ = WriteLogAsync(eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _ = WriteLogAsync(eventArgs.Data); };
            process.Exited += (_, _) => _ = OnExitedAsync(process);
            if (!process.Start()) throw new InvalidOperationException("Factorio process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            await SetStatusAsync(new(ServerState.Running, process.Id, DateTimeOffset.UtcNow, 0, null));
            return _status;
        }
        finally { _gate.Release(); }
    }

    public async Task<ServerStatus> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _manualStop = true;
            if (_process is null || _process.HasExited)
            {
                await SetStatusAsync(new(ServerState.Stopped, null, DateTimeOffset.UtcNow, 0, null));
                return _status;
            }
            var process = _process;
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
            var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, RedirectStandardError = true };
            info.ArgumentList.Add("--create");
            info.ArgumentList.Add(savePath);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Factorio could not create the new map.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Factorio could not create the save: {await process.StandardError.ReadToEndAsync(cancellationToken)}");
            await store.SetAsync("settings", settings with { ActiveSave = saveName }, cancellationToken);
            return saveName;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ReadLogsAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return _recentLogs.ToArray();
    }

    private async Task OnExitedAsync(Process exited)
    {
        var wasManual = _manualStop;
        var exitCode = exited.ExitCode;
        if (ReferenceEquals(_process, exited)) _process = null;
        if (wasManual)
        {
            await SetStatusAsync(new(ServerState.Stopped, null, DateTimeOffset.UtcNow, 0, null));
            return;
        }
        var attempt = Math.Min(_status.RestartAttempt + 1, 6);
        await SetStatusAsync(new(ServerState.Failed, null, DateTimeOffset.UtcNow, attempt, $"Factorio exited unexpectedly with code {exitCode}."));
        var delay = TimeSpan.FromSeconds(Math.Min(60, 2 << attempt));
        logger.LogWarning("Factorio exited with code {ExitCode}; restarting in {Delay}", exitCode, delay);
        await Task.Delay(delay);
        try { await StartAsync(CancellationToken.None); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatic Factorio restart failed");
            await SetStatusAsync(new(ServerState.Failed, null, DateTimeOffset.UtcNow, attempt, exception.Message));
        }
    }

    private async Task WriteServerSettingsAsync(ServerSettings settings, CancellationToken cancellationToken)
    {
        var document = new
        {
            name = settings.ServerName,
            description = settings.Description,
            max_players = settings.MaxPlayers,
            visibility = new { @public = settings.VisibilityPublic, lan = true },
            username = "",
            password = settings.ServerPassword ?? "",
            autosave_interval = settings.AutosaveMinutes,
            autosave_slots = 5,
            ignore_player_limit_for_returning_players = false,
            allow_commands = "admins-only",
            tags = new[] { "Docker", "Factorio Manager" }
        };
        await using var file = File.Create(Path.Combine(paths.Config, "server-settings.json"));
        await JsonSerializer.SerializeAsync(file, document, cancellationToken: cancellationToken);
    }

    private string GetExecutable(string version) => Path.Combine(paths.Versions, version, "bin", "x64", "factorio");

    private async Task WriteLogAsync(string line)
    {
        // Secrets are never expected in Factorio output; this removes the two known credential labels defensively.
        line = line.Replace("token=", "token=[redacted]", StringComparison.OrdinalIgnoreCase);
        _recentLogs.Enqueue($"{DateTimeOffset.UtcNow:O} {line}");
        while (_recentLogs.Count > 500) _recentLogs.TryDequeue(out _);
        await File.AppendAllTextAsync(Path.Combine(paths.Logs, "factorio.log"), $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        await hub.Clients.All.SendAsync("log", line);
    }

    private async Task SetStatusAsync(ServerStatus status)
    {
        _status = status;
        await hub.Clients.All.SendAsync("status", status);
    }
}
