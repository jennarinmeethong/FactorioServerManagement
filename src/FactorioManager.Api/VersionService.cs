using System.Diagnostics;
using System.Text.Json;

namespace FactorioManager.Api;

public sealed class VersionService(
    DataPaths paths,
    SecretStore secrets,
    StateStore store,
    ServerSupervisor supervisor,
    BackupService backups,
    IHttpClientFactory httpClientFactory,
    ILogger<VersionService> logger)
{
    public IReadOnlyList<string> GetCachedVersions() =>
        Directory.Exists(paths.Versions) ? Directory.EnumerateDirectories(paths.Versions).Select(Path.GetFileName).OfType<string>().OrderDescending().ToArray() : [];

    public async Task<JsonElement> GetCatalogAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClientFactory.CreateClient().GetAsync("https://factorio.com/api/latest-releases", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    public async Task<UpdateStatus> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        if (string.IsNullOrWhiteSpace(settings.ActiveVersion))
            return new UpdateStatus(null, null, DateTimeOffset.UtcNow, "Select a server version before checking for updates.");
        try
        {
            var catalog = await GetCatalogAsync(cancellationToken);
            var latest = catalog.GetProperty(settings.Channel).GetProperty("headless").GetString();
            var status = new UpdateStatus(latest == settings.ActiveVersion ? null : latest, settings.ActiveVersion, DateTimeOffset.UtcNow, null);
            await store.SetAsync("update_status", status, cancellationToken);
            return status;
        }
        catch (Exception exception)
        {
            var status = new UpdateStatus(null, settings.ActiveVersion, DateTimeOffset.UtcNow, exception.Message);
            await store.SetAsync("update_status", status, cancellationToken);
            return status;
        }
    }

    public async Task<UpdateStatus?> GetUpdateStatusAsync(CancellationToken cancellationToken) =>
        await store.GetAsync<UpdateStatus>("update_status", cancellationToken);

    public async Task<object> DownloadAsync(string channel, string version, CancellationToken cancellationToken)
    {
        if (channel is not ("stable" or "experimental") || !System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
            throw new InvalidOperationException("Channel or version is invalid.");
        var destination = Path.Combine(paths.Versions, version);
        if (File.Exists(Path.Combine(destination, "bin", "x64", "factorio"))) return new { version, downloaded = false, path = destination };
        var credentials = await secrets.ReadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(credentials.FactorioUsername) || string.IsNullOrWhiteSpace(credentials.FactorioToken))
            throw new InvalidOperationException("Configure a Factorio account and token during setup before downloading server versions.");

        var archive = Path.Combine(paths.Versions, $"{version}.tar.xz");
        var url = $"https://factorio.com/get-download/{Uri.EscapeDataString(version)}/headless/linux64?username={Uri.EscapeDataString(credentials.FactorioUsername)}&token={Uri.EscapeDataString(credentials.FactorioToken)}";
        using (var response = await httpClientFactory.CreateClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(archive);
            await input.CopyToAsync(output, cancellationToken);
        }
        try
        {
            Directory.CreateDirectory(destination);
            await RunTarAsync(archive, destination, cancellationToken);
            var extracted = Path.Combine(destination, "factorio");
            if (Directory.Exists(extracted))
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(extracted))
                    Directory.Move(item, Path.Combine(destination, Path.GetFileName(item)));
                Directory.Delete(extracted);
            }
            if (!File.Exists(Path.Combine(destination, "bin", "x64", "factorio")))
                throw new InvalidOperationException("The Factorio archive did not contain the expected Linux server binary.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(destination, "bin", "x64", "factorio"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return new { version, downloaded = true, path = destination };
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    public async Task<ServerStatus> ApplyAsync(VersionApplyRequest request, CancellationToken cancellationToken)
    {
        var versionPath = Path.Combine(paths.Versions, request.Version);
        if (request.Channel is not ("stable" or "experimental") || !File.Exists(Path.Combine(versionPath, "bin", "x64", "factorio")))
            throw new InvalidOperationException("Download the selected Factorio version before applying it.");
        var oldSettings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        var wasRunning = (await supervisor.GetStatusAsync()).State == ServerState.Running;
        if (wasRunning) await backups.CreateBackupAsync("pre-update", cancellationToken);
        if (wasRunning) await supervisor.StopAsync(cancellationToken);
        try
        {
            var updated = oldSettings with { ActiveVersion = request.Version, Channel = request.Channel };
            await store.SetAsync("settings", updated, cancellationToken);
            return wasRunning ? await supervisor.StartAsync(cancellationToken) : await supervisor.GetStatusAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start Factorio {Version}; restoring previous configuration", request.Version);
            await store.SetAsync("settings", oldSettings, cancellationToken);
            if (wasRunning && !string.IsNullOrWhiteSpace(oldSettings.ActiveVersion)) await supervisor.StartAsync(CancellationToken.None);
            throw new InvalidOperationException("The update could not start; the previous version configuration was restored.", exception);
        }
    }

    private static async Task RunTarAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("tar") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-xJf");
        info.ArgumentList.Add(archive);
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(destination);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The tar utility is required to extract Factorio downloads.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Factorio archive extraction failed: {await process.StandardError.ReadToEndAsync(cancellationToken)}");
    }
}
