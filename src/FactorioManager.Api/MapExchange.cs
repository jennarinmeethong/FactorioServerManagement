using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactorioManager.Api;

/// <summary>
/// Runs map exchange and preview work only in disposable Factorio data roots.  The
/// exchange codec itself deliberately stays in Factorio: 2.0's helpers API is the
/// only authority for parsing an exchange string.
/// </summary>
public sealed class NativeMapExchangeService(
    DataPaths paths,
    StateStore store,
    ServerSupervisor supervisor,
    MapControlCatalogService catalogs,
    NativeRuntimeDetector runtime,
    INativeProcessRunner processes,
    SourceRconClient rcon)
{
    private const long MaxNativeFileBytes = 4L * 1024 * 1024;
    private const long MaxSaveBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan NativeTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<MapExchangeImportResult> ImportAsync(MapExchangeImportRequest request, CancellationToken ct)
    {
        RequireStoppedAndConfirmation(request.ConfirmStopped);
        var saveName = SafeSaveName(request.SaveName);
        if (string.IsNullOrWhiteSpace(request.Exchange) || request.Exchange.Length > MaxNativeFileBytes || request.Exchange.Any(char.IsControl))
            throw new InvalidOperationException("The map exchange string is empty, malformed, or exceeds the allowed size.");
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(Path.Combine(paths.Saves, saveName))) throw new InvalidOperationException("A save with that name already exists.");
            var settings = await RequiredSettingsAsync(ct);
            await using var job = await PrepareJobAsync(settings, ct);
            var initialCompatibility = await CompatibilityAsync(settings, Hash(request.Exchange), ct);
            using var parsed = await ParseExchangeNativelyAsync(job, settings, request.Exchange, ct);
            ValidateParsedExchange(parsed, await catalogs.ResolveForSettingsAsync(settings, ct));
            await WriteParsedSettingsAsync(job, parsed, ct);
            var staged = Path.Combine(job.SavesPath, saveName);
            await RunAsync(settings, job, ["--create", staged, "--map-gen-settings", Path.Combine(job.OutputPath, "map-gen-settings.json"), "--map-settings", Path.Combine(job.OutputPath, "map-settings.json")], ct);
            RequireOutputFile(staged, MaxSaveBytes, "Factorio did not create a valid imported save.");
            await EnsureCompatibilityUnchangedAsync(initialCompatibility, ct);
            var target = Path.Combine(paths.Saves, saveName);
            AtomicPromote(staged, target);
            return new(saveName, initialCompatibility, "Imported save was promoted without changing the active save.");
        }
        finally { gate.Release(); }
    }

    public async Task<MapExchangeExportResult> ExportAsync(MapExchangeExportRequest request, CancellationToken ct)
    {
        RequireStoppedAndConfirmation(request.ConfirmStopped);
        var saveName = SafeSaveName(request.SaveName);
        await gate.WaitAsync(ct);
        try
        {
            var source = Path.Combine(paths.Saves, saveName);
            RequireOutputFile(source, MaxSaveBytes, "The selected save was not found or is invalid.");
            var sourceHash = await HashFileAsync(source, ct);
            var settings = await RequiredSettingsAsync(ct);
            var initialCompatibility = await CompatibilityAsync(settings, sourceHash, ct);
            await using var job = await PrepareJobAsync(settings, ct);
            var copy = Path.Combine(job.SavesPath, saveName);
            File.Copy(source, copy, overwrite: false);
            var exchange = await RunRconScriptAsync(job, settings, copy,
                "helpers.write_file('exchange.txt', game.get_map_exchange_string(game.surfaces[1].map_gen_settings), false)", "exchange.txt", ct);
            if (string.IsNullOrWhiteSpace(exchange) || exchange.Length > MaxNativeFileBytes || exchange.Any(char.IsControl))
                throw new InvalidOperationException("Factorio did not produce a valid map exchange string.");
            if (!string.Equals(sourceHash, await HashFileAsync(source, ct), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source save changed during native export; export was rejected.");
            await EnsureCompatibilityUnchangedAsync(initialCompatibility, ct);
            return new(exchange.Trim(), initialCompatibility);
        }
        finally { gate.Release(); }
    }

    public async Task<MapPreviewResult> PreviewAsync(MapPreviewRequest request, CancellationToken ct)
    {
        RequireStoppedAndConfirmation(request.ConfirmStopped);
        if (request.MapGeneration is null || MapGenerationSettingsValidator.Validate(request.MapGeneration).Count > 0)
            throw new InvalidOperationException("Map generation settings are invalid.");
        var planet = request.Planet.Trim();
        if (planet is not ("Nauvis" or "Vulcanus")) throw new InvalidOperationException("Only Nauvis and Vulcanus previews are supported.");
        if (request.Size is < 64 or > 1024 || !System.Text.RegularExpressions.Regex.IsMatch(request.Offset, @"^-?\d{1,7},-?\d{1,7}$"))
            throw new InvalidOperationException("Preview size or offset is invalid.");
        await gate.WaitAsync(ct);
        try
        {
            var settings = await RequiredSettingsAsync(ct);
            if (planet == "Vulcanus" && !string.Equals(settings.Expansion, "space-age", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Vulcanus preview requires the active Space Age expansion.");
            var catalog = await catalogs.ResolveForSettingsAsync(settings, ct);
            EnsureRequestedControlsAreKnown(request.MapGeneration, catalog);
            var normalized = MapControlCatalogService.Normalize(request.MapGeneration, catalog);
            var serializedSettings = MapGenerationSettingsJson.SerializeMapGeneration(normalized, catalog);
            var seed = request.Seed ?? normalized.Seed ?? RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var inputFingerprint = Hash(string.Join("\n", serializedSettings, planet, seed.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Offset));
            var initialCompatibility = await CompatibilityAsync(settings, inputFingerprint, ct);
            await using var job = await PrepareJobAsync(settings, ct);
            var settingsPath = Path.Combine(job.OutputPath, "map-gen-settings.json");
            await File.WriteAllTextAsync(settingsPath, serializedSettings, ct);
            var preview = Path.Combine(job.OutputPath, "preview.png");
            await RunAsync(settings, job, ["--generate-map-preview", preview, "--map-gen-settings", settingsPath, "--map-gen-seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "--map-preview-size", request.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), "--map-preview-offset", request.Offset, "--map-preview-planet", planet.ToLowerInvariant()], ct);
            var evidence = await new NativePreviewEvidenceVerifier().VerifyAsync(preview, ct);
            if (evidence.Length > MaxNativeFileBytes) throw new InvalidOperationException("The native preview exceeds the output limit.");
            var bytes = await File.ReadAllBytesAsync(preview, ct);
            await EnsureCompatibilityUnchangedAsync(initialCompatibility, ct);
            return new(planet, seed, request.Size, "image/png", Convert.ToBase64String(bytes), evidence.Length, evidence.Sha256, initialCompatibility);
        }
        finally { gate.Release(); }
    }

    public NativeRuntimeReadiness Readiness(ServerSettings settings) => runtime.Check(settings.ActiveVersion ?? string.Empty, settings.Expansion);

    private async Task<JsonDocument> ParseExchangeNativelyAsync(NativeJobDirectory job, ServerSettings settings, string exchange, CancellationToken ct)
    {
        // The literal is generated server-side and never concatenated from an untrusted command.
        var command = $"helpers.write_file('parsed.json', helpers.table_to_json(helpers.parse_map_exchange_string({LuaLiteral(exchange)})), false)";
        var json = await RunRconScriptAsync(job, settings, null, command, "parsed.json", ct);
        try { return JsonDocument.Parse(json); }
        catch (JsonException exception) { throw new InvalidOperationException("Factorio returned malformed parsed map settings.", exception); }
    }

    private async Task<string> RunRconScriptAsync(NativeJobDirectory job, ServerSettings settings, string? existingSave, string script, string expectedFile, CancellationToken ct)
    {
        var save = existingSave ?? Path.Combine(job.SavesPath, "bridge.zip");
        if (existingSave is null)
        {
            await File.WriteAllTextAsync(Path.Combine(job.OutputPath, "bridge-map-gen.json"), "{}", ct);
            await RunAsync(settings, job, ["--create", save, "--map-gen-settings", Path.Combine(job.OutputPath, "bridge-map-gen.json")], ct);
        }
        var port = FreePort(); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await File.WriteAllTextAsync(Path.Combine(job.OutputPath, "server-settings.json"), "{\"allow_commands\":\"admins-only\",\"name\":\"native-job\",\"description\":\"\",\"visibility\":{\"public\":false,\"lan\":false}}", ct);
        await using var server = StartServer(settings, job, save, port, password);
        var endpoint = new RconEndpoint(port, password);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { await rcon.ExecuteAsync(endpoint, "/c " + script, ct); break; }
            catch (RconUnavailableException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(250, ct); }
        }
        var path = Path.Combine(job.ScriptOutputPath, expectedFile);
        RequireOutputFile(path, MaxNativeFileBytes, "Factorio did not write the expected native job artifact.");
        return await File.ReadAllTextAsync(path, ct);
    }

    private NativeServerProcess StartServer(ServerSettings settings, NativeJobDirectory job, string save, int port, string password)
    {
        var ready = Readiness(settings);
        if (!ready.Ready) throw new InvalidOperationException(ready.Failure);
        var info = new ProcessStartInfo(ready.Requirements.ExecutablePath) { WorkingDirectory = job.Path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        AddIsolatedArguments(info, job);
        info.ArgumentList.Add("--start-server"); info.ArgumentList.Add(save); info.ArgumentList.Add("--server-settings"); info.ArgumentList.Add(Path.Combine(job.OutputPath, "server-settings.json"));
        info.ArgumentList.Add("--rcon-port"); info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture)); info.ArgumentList.Add("--rcon-password"); info.ArgumentList.Add(password);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The isolated Factorio native job could not start.");
        try
        {
            // The RCON bridge can run long enough to fill either redirected pipe.
            // Start both readers before making any network call so Factorio can
            // never block on stdout/stderr backpressure.
            return new NativeServerProcess(process,
                NativeProcessRunner.CaptureOutputAsync(process.StandardOutput),
                NativeProcessRunner.CaptureOutputAsync(process.StandardError));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            finally { process.Dispose(); }
            throw;
        }
    }

    private sealed class NativeServerProcess(Process process, Task<string> stdout, Task<string> stderr) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                Exception? terminationFailure = null;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception exception) { terminationFailure = exception; }
                await NativeProcessRunner.AwaitPostKillCleanupAsync(process.WaitForExitAsync, stdout, stderr);
                if (terminationFailure is not null)
                    throw new InvalidOperationException("The isolated Factorio server could not be terminated cleanly.", terminationFailure);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private async Task RunAsync(ServerSettings settings, NativeJobDirectory job, IEnumerable<string> args, CancellationToken ct)
    {
        var ready = Readiness(settings);
        if (!ready.Ready) throw new InvalidOperationException(ready.Failure);
        var all = new List<string> { "--config", job.ConfigFilePath, "--mod-directory", job.ModsPath, "--threads", "1" };
        all.AddRange(args);
        var result = await processes.RunAsync(new(ready.Requirements.ExecutablePath, all, job.Path, NativeTimeout), ct);
        if (result.TimedOut) throw new InvalidOperationException("The native Factorio job timed out and was terminated.");
        if (result.ExitCode != 0) throw new InvalidOperationException($"The native Factorio job failed (exit code {result.ExitCode}).");
    }

    private async Task<NativeJobDirectory> PrepareJobAsync(ServerSettings settings, CancellationToken ct)
    {
        var ready = Readiness(settings);
        if (!ready.Ready) throw new InvalidOperationException(ready.Failure);
        var job = NativeJobDirectory.Create(paths);
        try
        {
            await File.WriteAllTextAsync(job.ConfigFilePath, $"[path]\nread-data={ready.Requirements.DataPath}\nwrite-data={job.Path}\n[other]\ncheck-updates=false\n", ct);
            var mods = await EnabledModsAsync(ct);
            var builtIns = string.Equals(settings.Expansion, "space-age", StringComparison.OrdinalIgnoreCase)
                ? new[] { "base", "elevated-rails", "quality", "space-age" } : ["base"];
            var list = builtIns.Concat(mods.Select(x => x.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Select(x => new { name = x, enabled = true }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(job.ModsPath, "mod-list.json"), JsonSerializer.Serialize(new { mods = list }), ct);
            foreach (var mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.ArchiveFileName)) throw new InvalidOperationException($"Enabled mod '{mod.Name}' has no pinned archive.");
                var source = Path.Combine(paths.Mods, Path.GetFileName(mod.ArchiveFileName)); RequireOutputFile(source, 512L * 1024 * 1024, $"Enabled mod '{mod.Name}' archive is unavailable.");
                if (!string.IsNullOrWhiteSpace(mod.Sha256) && !string.Equals(await HashFileAsync(source, ct), mod.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Enabled mod '{mod.Name}' archive hash changed.");
                File.Copy(source, Path.Combine(job.ModsPath, Path.GetFileName(source)), overwrite: false);
            }
            return job;
        }
        catch { await job.DisposeAsync(); throw; }
    }

    private async Task<MapExchangeCompatibility> CompatibilityAsync(ServerSettings settings, string inputFingerprint, CancellationToken ct)
    {
        var catalog = await catalogs.ResolveForSettingsAsync(settings, ct);
        var mods = new List<EnabledModCompatibility>();
        foreach (var mod in await EnabledModsAsync(ct)) mods.Add(await ArchiveCompatibilityAsync(mod, ct));
        mods.Insert(0, new EnabledModCompatibility("base", settings.ActiveVersion!, null));
        if (string.Equals(settings.Expansion, "space-age", StringComparison.OrdinalIgnoreCase))
        {
            mods.Add(new("elevated-rails", settings.ActiveVersion!, null)); mods.Add(new("quality", settings.ActiveVersion!, null)); mods.Add(new("space-age", settings.ActiveVersion!, null));
        }
        return new(settings.ActiveVersion!, settings.Expansion, mods.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray(), catalog.ContentFingerprint, inputFingerprint);
    }

    private async Task<List<ModEntry>> EnabledModsAsync(CancellationToken ct) => (await store.GetAsync<ModEntry[]>("mods", ct) ?? []).Where(x => x.Enabled).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
    private async Task<EnabledModCompatibility> ArchiveCompatibilityAsync(ModEntry mod, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mod.ArchiveFileName)) throw new InvalidOperationException($"Enabled mod '{mod.Name}' has no pinned archive.");
        var source = Path.Combine(paths.Mods, Path.GetFileName(mod.ArchiveFileName));
        RequireOutputFile(source, 512L * 1024 * 1024, $"Enabled mod '{mod.Name}' archive is unavailable.");
        var archiveHash = await HashFileAsync(source, ct);
        if (!string.IsNullOrWhiteSpace(mod.Sha256) && !string.Equals(archiveHash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Enabled mod '{mod.Name}' archive hash changed.");
        return new EnabledModCompatibility(mod.Name, mod.Version, archiveHash);
    }
    private async Task<ServerSettings> RequiredSettingsAsync(CancellationToken ct)
    {
        var settings = await store.GetAsync<ServerSettings>("settings", ct) ?? new ServerSettings();
        if (string.IsNullOrWhiteSpace(settings.ActiveVersion)) throw new InvalidOperationException("Choose an active Factorio version first.");
        if (settings.Expansion is not ("vanilla" or "space-age")) throw new InvalidOperationException("The active expansion is unsupported.");
        if (!Readiness(settings).Ready) throw new InvalidOperationException(Readiness(settings).Failure);
        return settings;
    }
    private void RequireStoppedAndConfirmation(bool confirm) { if (!confirm) throw new InvalidOperationException("Explicit stopped-server confirmation is required."); if (supervisor.IsRunning) throw new InvalidOperationException("Stop the server before running map exchange or preview work."); }
    private static string SafeSaveName(string? value)
    {
        const string WindowsInvalidFileNameCharacters = "<>:\"/\\|?*";
        var name = value is null ? string.Empty : Path.GetFileName(value);
        var stem = Path.GetFileNameWithoutExtension(name);
        var windowsDeviceName = stem.Split('.', 2)[0].ToUpperInvariant();
        var reserved = windowsDeviceName is "CON" or "PRN" or "AUX" or "NUL"
            || (windowsDeviceName.Length == 4 && (windowsDeviceName.StartsWith("COM", StringComparison.Ordinal) || windowsDeviceName.StartsWith("LPT", StringComparison.Ordinal)) && windowsDeviceName[3] is >= '1' and <= '9');
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(stem) || value != name || name.Length > 100 || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || name.Any(char.IsControl) || name.Any(char.IsWhiteSpace) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.IndexOfAny(WindowsInvalidFileNameCharacters.ToCharArray()) >= 0 || reserved)
            throw new InvalidOperationException("Choose a safe .zip save name.");
        return name;
    }
    private async Task EnsureCompatibilityUnchangedAsync(MapExchangeCompatibility expected, CancellationToken ct)
    {
        var currentSettings = await RequiredSettingsAsync(ct);
        var current = await CompatibilityAsync(currentSettings, expected.InputFingerprint, ct);
        if (!string.Equals(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(current), StringComparison.Ordinal))
            throw new InvalidOperationException("Version, expansion, enabled-mod, or map-control compatibility changed during the native job.");
    }
    private static void RequireOutputFile(string path, long maximum, string message) { var info = File.Exists(path) ? new FileInfo(path) : null; if (info is null || info.Length == 0 || info.Length > maximum) throw new InvalidOperationException(message); }
    private static void AtomicPromote(string staged, string target) { if (File.Exists(target)) throw new InvalidOperationException("A save with that name already exists."); try { File.Move(staged, target); } catch (IOException) when (File.Exists(target)) { throw new InvalidOperationException("A save with that name already exists."); } }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static async Task<string> HashFileAsync(string path, CancellationToken ct) { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)); }
    private static int FreePort() { using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start(); return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
    private static string LuaLiteral(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    private static void AddIsolatedArguments(ProcessStartInfo info, NativeJobDirectory job) { info.ArgumentList.Add("--config"); info.ArgumentList.Add(job.ConfigFilePath); info.ArgumentList.Add("--mod-directory"); info.ArgumentList.Add(job.ModsPath); info.ArgumentList.Add("--threads"); info.ArgumentList.Add("1"); }
    private static void ValidateParsedExchange(JsonDocument parsed, MapControlCatalog catalog)
    {
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || !parsed.RootElement.TryGetProperty("map_gen_settings", out var generation) || !parsed.RootElement.TryGetProperty("map_settings", out var settings) || generation.ValueKind != JsonValueKind.Object || settings.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The map exchange data is incomplete.");
        if (generation.TryGetProperty("autoplace_controls", out var controls) && controls.ValueKind == JsonValueKind.Object)
        {
            var known = catalog.Controls.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (controls.EnumerateObject().Any(x => !known.Contains(x.Name))) throw new InvalidOperationException("The map exchange references unknown map controls for the active content.");
        }
    }
    private static void EnsureRequestedControlsAreKnown(MapGenerationSettings requested, MapControlCatalog catalog)
    {
        var known = catalog.Controls.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if ((requested.ControlOverrides ?? []).Keys.Any(id => string.IsNullOrWhiteSpace(id) || !known.Contains(id)))
            throw new InvalidOperationException("The preview references unknown map controls for the active content.");
    }
    private static async Task WriteParsedSettingsAsync(NativeJobDirectory job, JsonDocument parsed, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(job.OutputPath, "map-gen-settings.json"), parsed.RootElement.GetProperty("map_gen_settings").GetRawText(), ct);
        await File.WriteAllTextAsync(Path.Combine(job.OutputPath, "map-settings.json"), parsed.RootElement.GetProperty("map_settings").GetRawText(), ct);
    }
}
