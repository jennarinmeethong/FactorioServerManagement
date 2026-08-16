using System.IO.Compression;
using System.Text.Json;

namespace FactorioManager.Api;

public sealed class ModService(DataPaths paths, StateStore store, IHttpClientFactory clients)
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
        var mods = (await ListAsync(cancellationToken)).ToList();
        var index = mods.FindIndex(mod => string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException("The requested mod is not installed.");
        mods[index] = mods[index] with { Enabled = enabled };
        await store.SetAsync("mods", mods, cancellationToken);
        await WriteModListAsync(mods, cancellationToken);
        return mods;
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
}
