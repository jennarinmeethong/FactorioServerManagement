using System.IO.Compression;
using System.Text.Json;

namespace FactorioManager.Api;

public sealed class ModService(DataPaths paths, StateStore store, IHttpClientFactory clients, ServerSupervisor supervisor)
{
    public async Task<IReadOnlyList<ModEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        await store.GetAsync<List<ModEntry>>("mods", cancellationToken) ?? [];

    public async Task<JsonElement> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100) throw new InvalidOperationException("Enter a mod search term up to 100 characters.");
        var url = $"https://mods.factorio.com/api/mods?query={Uri.EscapeDataString(query)}&page_size=20";
        using var response = await clients.CreateClient().GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    public async Task<ModEntry> InstallAsync(ModInstallRequest request, CancellationToken cancellationToken)
    {
        if (supervisor.IsRunning) throw new InvalidOperationException("Stop the server before installing or changing mods.");
        if (!Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith("mods.factorio.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mod downloads must use an HTTPS URL from mods.factorio.com.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100) throw new InvalidOperationException("The mod name is invalid.");
        var temporary = Path.Combine(paths.Mods, $"{Guid.NewGuid():N}.zip");
        using (var response = await clients.CreateClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(temporary);
            await source.CopyToAsync(target, cancellationToken);
        }
        try
        {
            var metadata = ReadMetadata(temporary);
            if (!string.Equals(metadata.Name, request.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The downloaded archive does not match the requested mod.");
            var destination = Path.Combine(paths.Mods, $"{metadata.Name}_{metadata.Version}.zip");
            File.Move(temporary, destination, overwrite: true);
            var mods = (await ListAsync(cancellationToken)).Where(mod => !string.Equals(mod.Name, metadata.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var entry = new ModEntry(metadata.Name, metadata.Version, request.Enabled, metadata.Dependencies, DateTimeOffset.UtcNow);
            mods.Add(entry);
            await store.SetAsync("mods", mods, cancellationToken);
            await WriteModListAsync(mods, cancellationToken);
            return entry;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<ModEntry>> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        if (supervisor.IsRunning) throw new InvalidOperationException("Stop the server before installing or changing mods.");
        var mods = (await ListAsync(cancellationToken)).ToList();
        var index = mods.FindIndex(mod => string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException("The requested mod is not installed.");
        mods[index] = mods[index] with { Enabled = enabled };
        await store.SetAsync("mods", mods, cancellationToken);
        await WriteModListAsync(mods, cancellationToken);
        return mods;
    }

    public async Task<IReadOnlyList<ModUpdateInfo>> CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        var mods = await ListAsync(cancellationToken);
        var installed = mods.Select(mod => mod.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<ModUpdateInfo>(mods.Count);
        foreach (var mod in mods)
        {
            var details = await GetPortalDetailsAsync(mod.Name, cancellationToken);
            var latest = details.LatestVersion;
            var missing = mod.Dependencies
                .Select(DependencyName)
                .Where(name => name.Length > 0 && !string.Equals(name, "base", StringComparison.OrdinalIgnoreCase) && !installed.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            results.Add(new(mod.Name, mod.Version, latest, !string.IsNullOrWhiteSpace(latest) && !string.Equals(latest, mod.Version, StringComparison.OrdinalIgnoreCase), missing, details.DownloadUrl));
        }
        return results;
    }

    public async Task<ModEntry> UpdateAsync(string name, CancellationToken cancellationToken)
    {
        if (supervisor.IsRunning) throw new InvalidOperationException("Stop the server before installing or changing mods.");
        var current = (await ListAsync(cancellationToken)).FirstOrDefault(mod => string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The requested mod is not installed.");
        var details = await GetPortalDetailsAsync(current.Name, cancellationToken);
        if (string.IsNullOrWhiteSpace(details.DownloadUrl)) throw new InvalidOperationException("The Mod Portal has no downloadable release for this mod.");
        return await InstallAsync(new ModInstallRequest(current.Name, details.DownloadUrl, current.Enabled), cancellationToken);
    }

    private async Task WriteModListAsync(IReadOnlyList<ModEntry> mods, CancellationToken cancellationToken)
    {
        await using var file = File.Create(Path.Combine(paths.Mods, "mod-list.json"));
        await JsonSerializer.SerializeAsync(file, new { mods = mods.Select(mod => new { name = mod.Name, enabled = mod.Enabled }).Prepend(new { name = "base", enabled = true }) }, cancellationToken: cancellationToken);
    }

    private static (string Name, string Version, string[] Dependencies) ReadMetadata(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.Entries.FirstOrDefault(item => item.FullName.EndsWith("/info.json", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The mod archive has no info.json.");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var name = root.GetProperty("name").GetString() ?? throw new InvalidOperationException("The mod is missing a name.");
        var version = root.GetProperty("version").GetString() ?? throw new InvalidOperationException("The mod is missing a version.");
        var dependencies = root.TryGetProperty("dependencies", out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray() : [];
        return (name, version, dependencies);
    }

    private async Task<(string? LatestVersion, string? DownloadUrl)> GetPortalDetailsAsync(string name, CancellationToken cancellationToken)
    {
        var url = $"https://mods.factorio.com/api/mods/{Uri.EscapeDataString(name)}";
        using var response = await clients.CreateClient().GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("latest_release", out var release) || release.ValueKind != JsonValueKind.Object)
            return (null, null);
        var version = release.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
        var download = release.TryGetProperty("download_url", out var urlElement) ? urlElement.GetString() : null;
        return (version, download);
    }

    private static string DependencyName(string dependency)
    {
        var value = dependency.Trim();
        while (value.Length > 0 && "?!~&^".Contains(value[0])) value = value[1..].TrimStart();
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    }
}
