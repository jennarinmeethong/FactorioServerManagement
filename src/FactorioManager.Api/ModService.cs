using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioManager.Api;

public sealed class ModService(
    DataPaths paths,
    StateStore store,
    IHttpClientFactory? clients,
    ServerSupervisor supervisor,
    BackupService? backups = null,
    SecretStore? secrets = null)
{
    private const string ModPortalBaseUrl = "https://mods.factorio.com";
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 20_000;
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    private string Journal => Path.Combine(paths.Mods, ".mod-transaction.json");
    private string TransactionRoot => Path.Combine(paths.Mods, ".mod-transaction");
    private string QuarantineRoot => Path.Combine(paths.Mods, ".quarantine");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var mods = await ListAsync(ct);
        var changed = false;
        var migrated = new List<ModEntry>();
        foreach (var mod in mods)
        {
            var archiveName = mod.ArchiveFileName ?? CanonicalFileName(mod.Name, mod.Version);
            var archivePath = Path.Combine(paths.Mods, archiveName);
            if (mod.ArchiveFileName is null && File.Exists(archivePath))
            {
                await using var stream = File.OpenRead(archivePath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                migrated.Add(mod with { ArchiveFileName = archiveName, Sha256 = hash });
                changed = true;
            }
            else migrated.Add(mod);
        }
        if (changed) await store.SetAsync("mods", migrated, ct);
    }

    public async Task<IReadOnlyList<ModEntry>> ListAsync(CancellationToken ct = default) => await store.GetAsync<List<ModEntry>>("mods", ct) ?? [];

    public async Task<JsonElement> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100) throw new InvalidOperationException("Enter a mod search term up to 100 characters.");
        var settings = await store.GetAsync<ServerSettings>("settings", ct) ?? new ServerSettings();
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query.Trim(),
            ["page_size"] = 20,
            ["sort_attribute"] = "relevancy"
        };
        if (!string.IsNullOrWhiteSpace(settings.ActiveVersion)) payload["version"] = settings.ActiveVersion;
        if (secrets is not null)
        {
            var credentials = await secrets.ReadAsync(ct);
            if (!string.IsNullOrWhiteSpace(credentials.FactorioUsername) && !string.IsNullOrWhiteSpace(credentials.FactorioToken))
            {
                payload["username"] = credentials.FactorioUsername;
                payload["token"] = credentials.FactorioToken;
            }
        }

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Client().PostAsync($"{ModPortalBaseUrl}/api/search", content, ct);
        await EnsureRemoteSuccessAsync(response, "Mod Portal search");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Mod Portal returned an invalid search response.");
        return document.RootElement.Clone();
    }

    public async Task<ModRecoveryStatus> GetRecoveryStatusAsync(CancellationToken ct = default)
    {
        var quarantined = Directory.Exists(QuarantineRoot) ? Directory.EnumerateFiles(QuarantineRoot).Select(Path.GetFileName).OfType<string>().Order().ToArray() : [];
        if (!File.Exists(Journal)) return new(false, null, quarantined, null);
        try
        {
            await using var stream = File.OpenRead(Journal);
            var manifest = await JsonSerializer.DeserializeAsync<TransactionManifest>(stream, cancellationToken: ct);
            return manifest is null ? new(true, "The mod transaction journal is empty and requires recovery.", quarantined, null) : new(true, $"A {manifest.Operation} transaction is pending recovery.", quarantined, manifest.StartedAt);
        }
        catch (JsonException) { return new(true, "The mod transaction journal is malformed and has to be quarantined.", quarantined, null); }
    }

    public Task<ModEntry> InstallAsync(ModInstallRequest request, CancellationToken ct) => MutateAsync("install", async tx =>
    {
        ValidateName(request.Name);
        var staged = new List<StagedArchive>();
        var current = (await ListAsync(ct)).ToList();
        var root = await StagePortalAsync(request.Name, request.Version, ModSource.Portal, tx, ct);
        await ResolveDependenciesAsync(root.Metadata, current, staged, tx, request.IncludeDependencies, ct);
        staged.Add(root);
        var next = ReplaceMods(current, staged, request.Name, request.Enabled);
        await CommitAsync(tx, next, staged, ct);
        return next.First(m => m.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
    }, ct);

    public Task<ModEntry> UploadAsync(Stream content, string? fileName, ModUploadRequest request, CancellationToken ct) => MutateAsync("upload", async tx =>
    {
        var staged = new List<StagedArchive>();
        var current = (await ListAsync(ct)).ToList();
        var root = await StageLocalAsync(content, fileName, tx, ct);
        await ResolveDependenciesAsync(root.Metadata, current, staged, tx, request.IncludeDependencies, ct);
        staged.Add(root);
        var next = ReplaceMods(current, staged, root.Metadata.Name, request.Enabled);
        await CommitAsync(tx, next, staged, ct);
        return next.First(m => m.Name.Equals(root.Metadata.Name, StringComparison.OrdinalIgnoreCase));
    }, ct);

    public Task<IReadOnlyList<ModEntry>> SetEnabledAsync(string name, bool enabled, CancellationToken ct) => MutateAsync("toggle", async tx =>
    {
        var mods = (await ListAsync(ct)).ToList();
        var index = mods.FindIndex(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException("The requested mod is not installed.");
        mods[index] = mods[index] with { Enabled = enabled };
        await CommitAsync(tx, mods, [], ct);
        return (IReadOnlyList<ModEntry>)mods;
    }, ct);

    public Task<ModOperationResult> UninstallAsync(string name, bool force, CancellationToken ct) => MutateAsync("uninstall", async tx =>
    {
        var mods = (await ListAsync(ct)).ToList();
        var target = mods.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The requested mod is not installed.");
        var dependents = mods.Where(m => m.Enabled && m.Dependencies.Any(d => DependencyName(d).Equals(target.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (dependents.Length > 0 && !force) throw new InvalidOperationException($"Enabled dependents block uninstall: {string.Join(", ", dependents.Select(m => m.Name))}");
        if (force) foreach (var dependent in dependents)
        {
            var index = mods.FindIndex(m => m.Name.Equals(dependent.Name, StringComparison.OrdinalIgnoreCase));
            mods[index] = dependent with { Enabled = false };
        }
        mods.RemoveAll(m => m.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        await CommitAsync(tx, mods, [], ct);
        return new ModOperationResult("uninstall", true, "Mod uninstalled.", mods.ToArray(), force ? dependents.Select(m => m.Name).ToArray() : []);
    }, ct);

    public Task<ModEntry> UpdateAsync(string name, CancellationToken ct) => MutateAsync("update", async tx =>
    {
        var current = (await ListAsync(ct)).FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The requested mod is not installed.");
        var staged = new List<StagedArchive>();
        var replacement = await StagePortalAsync(current.Name, null, current.Source, tx, ct);
        var mods = (await ListAsync(ct)).Where(m => !m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        await ResolveDependenciesAsync(replacement.Metadata, mods, staged, tx, true, ct);
        staged.Add(replacement);
        mods.Add(ToEntry(replacement, current.Enabled));
        await CommitAsync(tx, mods, staged, ct);
        return mods.First(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }, ct);

    public async Task<IReadOnlyList<ModUpdateInfo>> CheckUpdatesAsync(CancellationToken ct)
    {
        var mods = await ListAsync(ct);
        var installed = mods.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModUpdateInfo>();
        foreach (var mod in mods)
        {
            var details = await PortalAsync(mod.Name, null, ct);
            var missing = mod.Dependencies.Select(DependencyName).Where(n => n.Length > 0 && !n.Equals("base", StringComparison.OrdinalIgnoreCase) && !installed.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            result.Add(new(mod.Name, mod.Version, details.Version, details.Version is not null && !details.Version.Equals(mod.Version, StringComparison.OrdinalIgnoreCase), missing, details.Url));
        }
        return result;
    }

    public Task<IReadOnlyList<ModEntry>> BulkUpdateAsync(string[] names, CancellationToken ct) => MutateAsync("bulk-update", async tx =>
    {
        var selected = names ?? [];
        var mods = (await ListAsync(ct)).ToList();
        var staged = new List<StagedArchive>();
        foreach (var current in mods.Where(m => selected.Length == 0 || selected.Contains(m.Name, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            var replacement = await StagePortalAsync(current.Name, null, current.Source, tx, ct);
            await ResolveDependenciesAsync(replacement.Metadata, mods, staged, tx, true, ct);
            staged.Add(replacement);
            var index = mods.FindIndex(m => m.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase));
            mods[index] = ToEntry(replacement, current.Enabled);
        }
        await CommitAsync(tx, mods, staged, ct);
        return (IReadOnlyList<ModEntry>)mods;
    }, ct);

    public async Task<IReadOnlyList<ModProfile>> ProfilesAsync(CancellationToken ct = default) => await store.GetAsync<List<ModProfile>>("mod_profiles", ct) ?? [];

    public Task<ModProfile> SaveProfileAsync(ModProfileRequest request, CancellationToken ct) => MutateAsync("profile-save", async _ =>
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) throw new InvalidOperationException("Profile name is invalid.");
        var profiles = (await ProfilesAsync(ct)).Where(p => !p.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var now = DateTimeOffset.UtcNow;
        var profile = new ModProfile(request.Name, request.Mods ?? [], now, now);
        profiles.Add(profile);
        await store.SetAsync("mod_profiles", profiles, ct);
        return profile;
    }, ct);

    public Task<IReadOnlyList<ModEntry>> ApplyProfileAsync(string name, CancellationToken ct) => MutateAsync("profile-apply", async tx =>
    {
        var profile = (await ProfilesAsync(ct)).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Profile not found.");
        var mods = (await ListAsync(ct)).ToList();
        var staged = new List<StagedArchive>();
        foreach (var wanted in profile.Mods)
        {
            ValidateName(wanted.Name);
            var current = mods.FirstOrDefault(m => m.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase));
            if (current is null || !current.Version.Equals(wanted.Version, StringComparison.OrdinalIgnoreCase))
            {
                var replacement = await StagePortalAsync(wanted.Name, wanted.Version, ModSource.Portal, tx, ct);
                await ResolveDependenciesAsync(replacement.Metadata, mods, staged, tx, true, ct);
                staged.Add(replacement);
                mods.RemoveAll(m => m.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase));
                mods.Add(ToEntry(replacement, wanted.Enabled));
            }
        }
        for (var i = 0; i < mods.Count; i++)
        {
            var wanted = profile.Mods.FirstOrDefault(x => x.Name.Equals(mods[i].Name, StringComparison.OrdinalIgnoreCase));
            mods[i] = wanted is null ? mods[i] with { Enabled = false } : mods[i] with { Enabled = wanted.Enabled };
        }
        await CommitAsync(tx, mods, staged, ct);
        return (IReadOnlyList<ModEntry>)mods;
    }, ct);

    public async Task<bool> DeleteProfileAsync(string name, CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        try
        {
            await RecoverAsync();
            if (supervisor.IsRunning) throw new InvalidOperationException("Stop the server before changing mod profiles.");
            var profiles = (await ProfilesAsync(ct)).ToList();
            var removed = profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            await store.SetAsync("mod_profiles", profiles, ct);
            return removed;
        }
        finally { mutationGate.Release(); }
    }

    public async Task<ModPreflightResult> PreflightAsync(ModPreflightRequest request, CancellationToken ct)
    {
        var settings = await store.GetAsync<ServerSettings>("settings", ct) ?? new ServerSettings();
        var mods = await ListAsync(ct);
        var blockers = new List<string>();
        var warnings = new List<string>();
        var downloads = new List<string>();
        if (supervisor.IsRunning) blockers.Add("Stop the server before changing mods.");
        if (File.Exists(Journal)) blockers.Add("A previous mod transaction requires recovery first.");
        if (request.Operation is "install" or "upload" or "enable" or "profile-apply" && string.IsNullOrWhiteSpace(settings.ActiveVersion)) warnings.Add("No active Factorio version is selected; enabling the mod will remain blocked until one is selected.");
        if (!string.IsNullOrWhiteSpace(settings.ActiveSave) && request.Operation is not "profile-save") warnings.Add($"A backup of {settings.ActiveSave} will be created before applying this change.");
        if (request.Operation is "install" or "upload") downloads.AddRange(request.Names ?? []);
        try { ValidateDependencyGraphForRuntime(mods); } catch (InvalidOperationException exception) { blockers.Add(exception.Message); }
        return new(request.Operation, blockers.Count == 0, !string.IsNullOrWhiteSpace(settings.ActiveSave), settings.ActiveSave, settings.ActiveVersion, mods.ToArray(), blockers.ToArray(), warnings.ToArray(), downloads.ToArray());
    }

    internal Task RecoverForTestingAsync() => RecoverAsync();

    public static void ValidateDependencyGraph(IReadOnlyList<ModEntry> mods)
    {
        ValidateDependencyGraphCore(mods, allowMissingOptional: false);
    }

    private static void ValidateDependencyGraphForRuntime(IReadOnlyList<ModEntry> mods) => ValidateDependencyGraphCore(mods, allowMissingOptional: true);

    private static void ValidateDependencyGraphCore(IReadOnlyList<ModEntry> mods, bool allowMissingOptional)
    {
        var map = mods.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Where(m => m.Enabled)) Validate(mod);
        void Validate(ModEntry mod)
        {
            if (!visiting.Add(mod.Name)) throw new InvalidOperationException("Mod dependency cycle detected.");
            if (!visited.Add(mod.Name)) { visiting.Remove(mod.Name); return; }
            foreach (var dependency in mod.Dependencies.Select(ParseDependency))
            {
                if (dependency.Name.Equals("base", StringComparison.OrdinalIgnoreCase)) continue;
                if (dependency.Incompatible)
                {
                    if (map.TryGetValue(dependency.Name, out var conflict) && conflict.Enabled) throw new InvalidOperationException($"Mod conflict detected: {mod.Name} conflicts with {dependency.Name}.");
                    continue;
                }
                if (!map.TryGetValue(dependency.Name, out var target) || !target.Enabled)
                {
                    if (!dependency.Optional || !allowMissingOptional) throw new InvalidOperationException($"Missing dependency: {dependency.Name}");
                    continue;
                }
                if (!Satisfies(target.Version, dependency)) throw new InvalidOperationException($"Dependency version mismatch: {mod.Name} requires {dependency.Name} {dependency.Operator}{dependency.Version}.");
                Validate(target);
            }
            visiting.Remove(mod.Name);
        }
    }

    private async Task<T> MutateAsync<T>(string operation, Func<TransactionManifest, Task<T>> action, CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct);
        TransactionManifest? manifest = null;
        try
        {
            await RecoverAsync();
            if (supervisor.IsRunning) throw new InvalidOperationException("Stop the server before installing or changing mods.");
            manifest = await BeginTransactionAsync(operation, ct);
            var result = await action(manifest);
            await CompleteTransactionAsync(manifest);
            return result;
        }
        catch
        {
            if (manifest is not null)
            {
                try { await RestoreManifestAsync(manifest, CancellationToken.None); } catch { }
            }
            throw;
        }
        finally { mutationGate.Release(); }
    }

    private async Task<TransactionManifest> BeginTransactionAsync(string operation, CancellationToken ct)
    {
        paths.EnsureCreated();
        Directory.CreateDirectory(TransactionRoot);
        var root = Path.Combine(TransactionRoot, Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(root, "archives");
        Directory.CreateDirectory(archiveRoot);
        var archiveBackups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in Directory.EnumerateFiles(paths.Mods, "*.zip"))
        {
            var name = Path.GetFileName(archive);
            var backup = Path.Combine(archiveRoot, name);
            File.Copy(archive, backup, true);
            archiveBackups[name] = backup;
        }
        var list = Path.Combine(paths.Mods, "mod-list.json");
        string? listBackup = null;
        if (File.Exists(list)) { listBackup = Path.Combine(root, "mod-list.json"); File.Copy(list, listBackup, true); }
        var manifest = new TransactionManifest(operation, DateTimeOffset.UtcNow, root, archiveBackups, listBackup, (await ListAsync(ct)).ToArray(), (await ProfilesAsync(ct)).ToArray(), "prepared");
        await File.WriteAllTextAsync(Journal, JsonSerializer.Serialize(manifest), ct);
        return manifest;
    }

    private async Task CommitAsync(TransactionManifest manifest, IReadOnlyList<ModEntry> mods, IReadOnlyList<StagedArchive> staged, CancellationToken ct)
    {
        ValidateCompatibility(mods, await ActiveVersionAsync(ct));
        ValidateDependencyGraphForRuntime(mods);
        await EnsureSaveBackupAsync(mods, manifest, ct);
        foreach (var archive in staged) File.Move(archive.Path, Path.Combine(paths.Mods, archive.Metadata.CanonicalFileName), true);
        var desired = mods.Select(m => m.ArchiveFileName ?? CanonicalFileName(m.Name, m.Version)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in Directory.EnumerateFiles(paths.Mods, "*.zip")) if (!desired.Contains(Path.GetFileName(archive))) await QuarantineAsync(archive, "orphan", ct);
        var listPath = Path.Combine(paths.Mods, "mod-list.json");
        var temporaryList = listPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporaryList, JsonSerializer.Serialize(new { mods = mods.Select(m => new { name = m.Name, enabled = m.Enabled }).Prepend(new { name = "base", enabled = true }) }), ct);
        await store.SetManyAsync(new Dictionary<string, object?> { ["mods"] = mods.ToArray(), ["mod_profiles"] = await ProfilesAsync(ct) }, ct);
        File.Move(temporaryList, listPath, true);
        foreach (var mod in mods)
        {
            var file = Path.Combine(paths.Mods, mod.ArchiveFileName ?? CanonicalFileName(mod.Name, mod.Version));
            if (!File.Exists(file)) throw new InvalidOperationException($"Archive promotion failed for {mod.Name}.");
        }
    }

    private async Task EnsureSaveBackupAsync(IReadOnlyList<ModEntry> next, TransactionManifest manifest, CancellationToken ct)
    {
        var settings = await store.GetAsync<ServerSettings>("settings", ct) ?? new ServerSettings();
        if (string.IsNullOrWhiteSpace(settings.ActiveSave) || backups is null) return;
        var previous = manifest.Mods.Where(m => m.Enabled).ToDictionary(m => m.Name, m => m.Version, StringComparer.OrdinalIgnoreCase);
        var current = next.Where(m => m.Enabled).ToDictionary(m => m.Name, m => m.Version, StringComparer.OrdinalIgnoreCase);
        if (previous.Count == current.Count && previous.All(x => current.TryGetValue(x.Key, out var version) && version == x.Value)) return;
        await backups.CreateBackupAsync("pre-mod-change", ct);
    }

    private async Task RestoreManifestAsync(TransactionManifest manifest, CancellationToken ct)
    {
        var root = Contained(TransactionRoot, manifest.BackupDirectory);
        if (!Directory.Exists(root)) throw new InvalidOperationException("The mod transaction backup directory is missing.");
        foreach (var archive in Directory.EnumerateFiles(paths.Mods, "*.zip"))
        {
            if (manifest.ArchiveBackups.ContainsKey(Path.GetFileName(archive))) File.Delete(archive);
            else await QuarantineAsync(archive, "recovery-orphan", ct);
        }
        foreach (var item in manifest.ArchiveBackups)
        {
            var source = Contained(root, item.Value);
            File.Copy(source, Contained(paths.Mods, item.Key), true);
        }
        var list = Path.Combine(paths.Mods, "mod-list.json");
        if (manifest.ModListBackup is null) { if (File.Exists(list)) File.Delete(list); } else File.Copy(Contained(root, manifest.ModListBackup), list, true);
        await store.SetManyAsync(new Dictionary<string, object?> { ["mods"] = manifest.Mods, ["mod_profiles"] = manifest.Profiles }, ct);
        await CompleteTransactionAsync(manifest);
    }

    private async Task RecoverAsync()
    {
        if (!File.Exists(Journal)) return;
        TransactionManifest manifest;
        try
        {
            await using var stream = File.OpenRead(Journal);
            manifest = await JsonSerializer.DeserializeAsync<TransactionManifest>(stream) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            var quarantine = Journal + ".quarantine";
            if (File.Exists(quarantine)) quarantine += "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.Move(Journal, quarantine, true);
            throw new InvalidOperationException("The mod transaction journal was malformed and was quarantined for recovery.");
        }
        if (!string.IsNullOrWhiteSpace(manifest.BackupDirectory))
        {
            await RestoreManifestAsync(manifest, CancellationToken.None);
            return;
        }
        await RecoverLegacyJournalAsync(CancellationToken.None);
    }

    private async Task RecoverLegacyJournalAsync(CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Journal, ct));
        var root = document.RootElement;
        if (root.TryGetProperty("mods", out var mods)) await store.SetAsync("mods", JsonSerializer.Deserialize<ModEntry[]>(mods.GetRawText()) ?? [], ct);
        if (root.TryGetProperty("profiles", out var profiles)) await store.SetAsync("mod_profiles", JsonSerializer.Deserialize<ModProfile[]>(profiles.GetRawText()) ?? [], ct);
        if (root.TryGetProperty("archives", out var archives))
        {
            foreach (var archive in archives.EnumerateObject())
            {
                var name = Path.GetFileName(archive.Name);
                if (name != archive.Name || archive.Value.ValueKind != JsonValueKind.String) continue;
                await File.WriteAllBytesAsync(Path.Combine(paths.Mods, name), Convert.FromBase64String(archive.Value.GetString() ?? ""), ct);
            }
        }
        var list = Path.Combine(paths.Mods, "mod-list.json");
        if (root.TryGetProperty("modList", out var encoded) && encoded.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(encoded.GetString())) await File.WriteAllBytesAsync(list, Convert.FromBase64String(encoded.GetString()!), ct);
        else if (File.Exists(list)) File.Delete(list);
        File.Delete(Journal);
    }

    private async Task CompleteTransactionAsync(TransactionManifest manifest)
    {
        if (File.Exists(Journal)) File.Delete(Journal);
        if (Directory.Exists(manifest.BackupDirectory)) Directory.Delete(manifest.BackupDirectory, true);
        await Task.CompletedTask;
    }

    private async Task<StagedArchive> StagePortalAsync(string name, string? version, ModSource source, TransactionManifest transaction, CancellationToken ct)
    {
        var release = await PortalAsync(name, version, ct);
        if (release.Url is null) throw new InvalidOperationException($"The Mod Portal has no downloadable release for {name}.");
        var path = await DownloadAsync(release.Url, transaction, ct);
        var metadata = await ReadMetadataAsync(path, ct);
        if (!metadata.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Downloaded metadata does not match the requested mod.");
        if (version is not null && !metadata.Version.Equals(version, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"The Mod Portal has no compatible release for {name} version {version}.");
        return new(path, metadata, source);
    }

    private async Task<StagedArchive> StageLocalAsync(Stream content, string? fileName, TransactionManifest transaction, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Upload a .zip mod archive with a safe file name.");
        var path = Path.Combine(transaction.BackupDirectory, "staging", Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await CopyLimitedAsync(content, path, ct);
        var metadata = await ReadMetadataAsync(path, ct);
        return new(path, metadata, ModSource.Local);
    }

    private async Task ResolveDependenciesAsync(ModMetadata metadata, List<ModEntry> installed, List<StagedArchive> staged, TransactionManifest transaction, bool includeDependencies, CancellationToken ct, HashSet<string>? resolving = null)
    {
        resolving ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!resolving.Add(metadata.Name)) throw new InvalidOperationException($"Mod dependency cycle detected at {metadata.Name}.");
        foreach (var dependency in metadata.Dependencies.Select(ParseDependency).Where(x => !x.Incompatible && !x.Name.Equals("base", StringComparison.OrdinalIgnoreCase)))
        {
            var existing = staged.Select(x => x.Metadata).FirstOrDefault(x => x.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase));
            var installedEntry = installed.FirstOrDefault(x => x.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null || installedEntry is not null && Satisfies(installedEntry.Version, dependency))
            {
                if (installedEntry is not null && !installedEntry.Enabled && !dependency.Optional) installed[installed.IndexOf(installedEntry)] = installedEntry with { Enabled = true };
                continue;
            }
            if (dependency.Optional || !includeDependencies) { if (!dependency.Optional) throw new InvalidOperationException($"Missing dependency: {dependency.Name}"); continue; }
            var dependencyArchive = await StagePortalAsync(dependency.Name, dependency.Version, ModSource.Portal, transaction, ct);
            await ResolveDependenciesAsync(dependencyArchive.Metadata, installed, staged, transaction, true, ct, resolving);
            staged.Add(dependencyArchive);
            installed.RemoveAll(x => x.Name.Equals(dependencyArchive.Metadata.Name, StringComparison.OrdinalIgnoreCase));
            installed.Add(ToEntry(dependencyArchive, true));
        }
        resolving.Remove(metadata.Name);
    }

    private static List<ModEntry> ReplaceMods(List<ModEntry> current, IReadOnlyList<StagedArchive> staged, string rootName, bool enabled)
    {
        foreach (var archive in staged) current.RemoveAll(m => m.Name.Equals(archive.Metadata.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var archive in staged) current.Add(ToEntry(archive, archive.Metadata.Name.Equals(rootName, StringComparison.OrdinalIgnoreCase) ? enabled : true));
        return current;
    }

    private static ModEntry ToEntry(StagedArchive archive, bool enabled) => new(archive.Metadata.Name, archive.Metadata.Version, enabled, archive.Metadata.Dependencies, DateTimeOffset.UtcNow, archive.Source, archive.Metadata.CanonicalFileName, archive.Metadata.Sha256, archive.Metadata.FactorioVersionRequirement);

    private async Task<(string? Version, string? Url)> PortalAsync(string name, string? version, CancellationToken ct)
    {
        ValidateName(name);
        using var response = await Client().GetAsync($"https://mods.factorio.com/api/mods/{Uri.EscapeDataString(name)}", ct);
        await EnsureRemoteSuccessAsync(response, "Mod Portal lookup");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("latest_release", out var release)) return (null, null);
        var latest = release.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
        var url = release.TryGetProperty("download_url", out var urlValue) ? urlValue.GetString() : null;
        if (version is not null && !string.Equals(latest, version, StringComparison.OrdinalIgnoreCase)) return (latest, null);
        return (latest, url);
    }

    private async Task<string> DownloadAsync(string url, TransactionManifest transaction, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var parsed)) throw new InvalidOperationException("The Mod Portal returned an invalid download URL.");
        var uri = parsed.IsAbsoluteUri ? parsed : new Uri(new Uri(ModPortalBaseUrl), url);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("mods.factorio.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Untrusted Mod Portal download URL.");
        if (secrets is null) throw new InvalidOperationException("Configure a Factorio account and token during setup before installing Mod Portal mods.");
        var credentials = await secrets.ReadAsync(ct);
        if (string.IsNullOrWhiteSpace(credentials.FactorioUsername) || string.IsNullOrWhiteSpace(credentials.FactorioToken))
            throw new InvalidOperationException("Configure a Factorio account and token during setup before installing Mod Portal mods.");
        var query = uri.Query.TrimStart('?');
        var authQuery = $"username={Uri.EscapeDataString(credentials.FactorioUsername)}&token={Uri.EscapeDataString(credentials.FactorioToken)}";
        uri = new UriBuilder(uri) { Query = string.IsNullOrWhiteSpace(query) ? authQuery : $"{query}&{authQuery}" }.Uri;
        var path = Path.Combine(transaction.BackupDirectory, "staging", Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var response = await Client().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureRemoteSuccessAsync(response, "Mod Portal download");
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("Mod Portal returned a login page instead of a mod archive. Check the Factorio credentials.");
        if (response.Content.Headers.ContentLength > MaxArchiveBytes) throw new InvalidOperationException("The mod archive exceeds the 512 MB limit.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(path);
        await CopyLimitedAsync(source, destination, MaxArchiveBytes, ct);
        return path;
    }

    private static async Task EnsureRemoteSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync();
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.TryGetProperty("message", out var property)) message = property.GetString();
            if (document.RootElement.TryGetProperty("error", out property)) message ??= property.GetString();
        }
        catch (JsonException) { }
        throw new HttpRequestException($"{operation} failed with HTTP {(int)response.StatusCode}{(string.IsNullOrWhiteSpace(message) ? "." : $": {message}")}", null, response.StatusCode);
    }

    private static async Task<ModMetadata> ReadMetadataAsync(string path, CancellationToken ct)
    {
        var infoEntries = new List<ZipArchiveEntry>();
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(path); }
        catch (InvalidDataException exception) { throw new InvalidOperationException("The mod archive is not a valid zip file.", exception); }
        using (archive)
        {
        if (archive.Entries.Count > MaxArchiveEntries) throw new InvalidOperationException("The mod archive contains too many files.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            expanded += entry.Length;
            if (expanded > MaxExpandedBytes) throw new InvalidOperationException("The mod archive expands beyond the safe limit.");
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == "..")) throw new InvalidOperationException("The mod archive contains an unsafe path.");
            if (normalized.EndsWith("/info.json", StringComparison.OrdinalIgnoreCase) || normalized.Equals("info.json", StringComparison.OrdinalIgnoreCase)) infoEntries.Add(entry);
        }
        if (infoEntries.Count != 1) throw new InvalidOperationException("The mod archive must contain exactly one info.json.");
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(infoEntries[0].Open(), cancellationToken: ct); }
        catch (JsonException exception) { throw new InvalidOperationException("The mod info.json is malformed.", exception); }
        using (document)
        {
        var root = document.RootElement;
        var name = root.GetProperty("name").GetString() ?? throw new InvalidOperationException("Mod info.json has no name.");
        var version = root.GetProperty("version").GetString() ?? throw new InvalidOperationException("Mod info.json has no version.");
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(version) || version.Length > 32 || version.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))) throw new InvalidOperationException("The mod archive version is invalid.");
        var dependencies = root.TryGetProperty("dependencies", out var dependencyElement) && dependencyElement.ValueKind == JsonValueKind.Array ? dependencyElement.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
        var factorioVersion = root.TryGetProperty("factorio_version", out var factorioElement) ? factorioElement.GetString() : null;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return new(name, version, dependencies, factorioVersion, CanonicalFileName(name, version), hash);
        }
        }
    }

    private async Task<string?> ActiveVersionAsync(CancellationToken ct) => (await store.GetAsync<ServerSettings>("settings", ct))?.ActiveVersion;

    private static void ValidateCompatibility(IEnumerable<ModEntry> mods, string? activeVersion)
    {
        foreach (var mod in mods.Where(m => m.Enabled))
        {
            if (string.IsNullOrWhiteSpace(mod.FactorioVersionRequirement)) continue;
            if (string.IsNullOrWhiteSpace(activeVersion)) throw new InvalidOperationException($"Factorio version is required before enabling {mod.Name}.");
            var required = mod.FactorioVersionRequirement.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var active = activeVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (required.Length >= 2 && active.Length >= 2 && (required[0] != active[0] || required[1] != active[1])) throw new InvalidOperationException($"Mod {mod.Name} requires Factorio {mod.FactorioVersionRequirement}; active version is {activeVersion}.");
        }
    }

    private static DependencySpec ParseDependency(string raw)
    {
        var value = raw.Trim();
        var optional = value.StartsWith("?", StringComparison.Ordinal) || value.StartsWith("~", StringComparison.Ordinal);
        var incompatible = value.StartsWith("!", StringComparison.Ordinal);
        value = value.TrimStart('?', '~', '!', '&', '^').Trim();
        var match = Regex.Match(value, "^(?<name>[^\\s<>=]+)\\s*(?:(?<operator>>=|<=|>|<|=)\\s*(?<version>[0-9A-Za-z._-]+))?$");
        if (!match.Success) return new(value, null, null, optional, incompatible);
        return new(match.Groups["name"].Value, match.Groups["operator"].Success ? match.Groups["operator"].Value : null, match.Groups["version"].Success ? match.Groups["version"].Value : null, optional, incompatible);
    }

    private static bool Satisfies(string actual, DependencySpec dependency)
    {
        if (dependency.Version is null || dependency.Operator is null) return true;
        var left = CompareVersions(actual, dependency.Version);
        return dependency.Operator switch { "=" => left == 0, ">" => left > 0, ">=" => left >= 0, "<" => left < 0, "<=" => left <= 0, _ => false };
    }

    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('.', StringSplitOptions.RemoveEmptyEntries); var b = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length && int.TryParse(a[i], out var ai) ? ai : 0; var bv = i < b.Length && int.TryParse(b[i], out var bi) ? bi : 0;
            var compared = av.CompareTo(bv); if (compared != 0) return compared;
        }
        return 0;
    }

    private static string DependencyName(string dependency) => ParseDependency(dependency).Name;
    private static string CanonicalFileName(string name, string version) => $"{name}_{version}.zip";
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar) || name.Contains("..", StringComparison.Ordinal)) throw new InvalidOperationException("The mod name is invalid."); }

    private static async Task CopyLimitedAsync(Stream source, string destination, CancellationToken ct)
    {
        await using var output = File.Create(destination); await CopyLimitedAsync(source, output, MaxArchiveBytes, ct);
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024]; long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct); if (read == 0) break; total += read;
            if (total > limit) throw new InvalidOperationException("The mod archive exceeds the 512 MB limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private async Task QuarantineAsync(string path, string reason, CancellationToken ct)
    {
        Directory.CreateDirectory(QuarantineRoot);
        var destination = Path.Combine(QuarantineRoot, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{reason}-{Path.GetFileName(path)}");
        File.Move(path, destination, true); await Task.CompletedTask;
    }

    private static string Contained(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; var full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The transaction path is invalid.");
        return full;
    }

    private HttpClient Client() => clients?.CreateClient() ?? throw new InvalidOperationException("HTTP client is unavailable.");
    private sealed record DependencySpec(string Name, string? Operator, string? Version, bool Optional, bool Incompatible);
    private sealed record ModMetadata(string Name, string Version, string[] Dependencies, string? FactorioVersionRequirement, string CanonicalFileName, string Sha256);
    private sealed record StagedArchive(string Path, ModMetadata Metadata, ModSource Source);
    private sealed record TransactionManifest(string Operation, DateTimeOffset StartedAt, string BackupDirectory, Dictionary<string, string> ArchiveBackups, string? ModListBackup, ModEntry[] Mods, ModProfile[] Profiles, string Phase);
}
