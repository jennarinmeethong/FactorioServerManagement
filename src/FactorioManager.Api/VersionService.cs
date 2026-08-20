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
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public IReadOnlyList<string> GetCachedVersions() =>
        Directory.Exists(paths.Versions) ? Directory.EnumerateDirectories(paths.Versions).Select(Path.GetFileName).OfType<string>().OrderDescending().ToArray() : [];

    public async Task<JsonElement> GetCatalogAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClientFactory.CreateClient().GetAsync("https://factorio.com/api/latest-releases", cancellationToken);
        await EnsureRemoteSuccessAsync(response, "Factorio release catalog");
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
        await DownloadGate.WaitAsync(cancellationToken);
        var destination = Path.Combine(paths.Versions, version);
        var temporaryRoot = Path.Combine(paths.Versions, $".download-{version}-{Guid.NewGuid():N}");
        var archive = Path.Combine(temporaryRoot, "factorio.tar.xz");
        var extractRoot = Path.Combine(temporaryRoot, "extract");
        try
        {
            if (File.Exists(Path.Combine(destination, "bin", "x64", "factorio"))) return new { version, downloaded = false, path = destination };
            var credentials = await secrets.ReadAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(credentials.FactorioUsername) || string.IsNullOrWhiteSpace(credentials.FactorioToken))
                throw new InvalidOperationException("Configure a Factorio account and token during setup before downloading server versions.");

            Directory.CreateDirectory(temporaryRoot);
            var url = $"https://factorio.com/get-download/{Uri.EscapeDataString(version)}/headless/linux64?username={Uri.EscapeDataString(credentials.FactorioUsername)}&token={Uri.EscapeDataString(credentials.FactorioToken)}";
            using (var response = await httpClientFactory.CreateClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                await EnsureRemoteSuccessAsync(response, "Factorio server download");
                if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException("Factorio returned a login page instead of the server archive. Check the Factorio credentials.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(archive);
                await input.CopyToAsync(output, cancellationToken);
            }
            var gzip = await ValidateArchiveAsync(archive, cancellationToken);
            Directory.CreateDirectory(extractRoot);
            await RunTarAsync(archive, extractRoot, cancellationToken, gzip);
            var extracted = Path.Combine(extractRoot, "factorio");
            var sourceRoot = Directory.Exists(extracted) ? extracted : extractRoot;
            var binary = Path.Combine(sourceRoot, "bin", "x64", "factorio");
            if (!File.Exists(binary))
                throw new InvalidOperationException("The Factorio archive did not contain the expected Linux server binary.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(sourceRoot, destination);
            return new { version, downloaded = true, path = destination };
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            DownloadGate.Release();
        }
    }

    private static async Task<bool> ValidateArchiveAsync(string archive, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(archive);
        var header = new byte[6];
        var read = await stream.ReadAsync(header, cancellationToken);
        if (read == header.Length && header.SequenceEqual(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 })) return false;
        if (read >= 2 && header[0] == 0x1F && header[1] == 0x8B) return true;
        throw new InvalidOperationException("Factorio returned an invalid server archive. Check the selected version and credentials.");
    }

    private static async Task EnsureRemoteSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException($"{operation} failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private static async Task RunTarAsync(string archive, string destination, CancellationToken cancellationToken, bool gzip)
    {
        var info = new ProcessStartInfo("tar") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(gzip ? "-xzf" : "-xJf");
        info.ArgumentList.Add(archive);
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(destination);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The tar utility is required to extract Factorio downloads.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Factorio archive extraction failed: {await process.StandardError.ReadToEndAsync(cancellationToken)}");
    }

    public async Task<ServerStatus> ApplyAsync(VersionApplyRequest request, CancellationToken cancellationToken)
    {
        var versionPath = Path.Combine(paths.Versions, request.Version);
        if (request.Channel is not ("stable" or "experimental") || !File.Exists(Path.Combine(versionPath, "bin", "x64", "factorio")))
            throw new InvalidOperationException("Download the selected Factorio version before applying it.");
        var oldSettings = await store.GetAsync<ServerSettings>("settings", cancellationToken) ?? new ServerSettings();
        var wasRunning = (await supervisor.GetStatusAsync()).State == ServerState.Running;
        // Always create a recovery point before changing the active binary. If a save is selected,
        // refusing to continue when it cannot be backed up is safer than applying a version blindly.
        if (!string.IsNullOrWhiteSpace(oldSettings.ActiveSave)) await backups.CreateBackupAsync("pre-update", cancellationToken);
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

}
