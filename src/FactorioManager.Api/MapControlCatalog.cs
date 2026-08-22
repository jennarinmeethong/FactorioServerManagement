using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FactorioManager.Api;

public sealed record MapControlOverride(string? Frequency = null, string? Size = null, string? Richness = null);
public sealed record MapControlCatalogEntry(string Id, string Surface, string Category, bool Richness, bool CanBeDisabled);
public sealed record MapControlCatalog(string Version, string ContentFingerprint, string[] Surfaces, MapControlCatalogEntry[] Controls);

/// <summary>Discovers only declared Factorio autoplace controls and applies the reviewed surface manifest.</summary>
public sealed class MapControlCatalogService(DataPaths paths)
{
    public static readonly string[] SupportedSurfaces = ["Nauvis", "Vulcanus", "Gleba", "Fulgora", "Aquilo"];
    private static readonly IReadOnlyDictionary<string, string> SurfaceManifest = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["iron-ore"] = "Nauvis", ["copper-ore"] = "Nauvis", ["stone"] = "Nauvis", ["coal"] = "Nauvis", ["crude-oil"] = "Nauvis", ["uranium-ore"] = "Nauvis",
        ["water"] = "Nauvis", ["trees"] = "Nauvis", ["rocks"] = "Nauvis", ["starting_area_moisture"] = "Nauvis", ["nauvis_cliff"] = "Nauvis", ["enemy-base"] = "Nauvis",
        ["vulcanus_coal"] = "Vulcanus", ["tungsten_ore"] = "Vulcanus", ["calcite"] = "Vulcanus", ["sulfuric_acid_geyser"] = "Vulcanus", ["vulcanus_volcanism"] = "Vulcanus",
        ["gleba_stone"] = "Gleba", ["gleba_cliff"] = "Gleba", ["gleba_water"] = "Gleba", ["gleba_plants"] = "Gleba", ["gleba_enemy_base"] = "Gleba",
        ["scrap"] = "Fulgora", ["fulgora_cliff"] = "Fulgora", ["fulgora_islands"] = "Fulgora",
        ["aquilo_crude_oil"] = "Aquilo", ["fluorine_vent"] = "Aquilo", ["lithium-brine"] = "Aquilo", ["lithium_brine"] = "Aquilo"
    };
    private static readonly Regex Block = new("\\{(?:(?!\\n\\s*\\}).)*?name\\s*=\\s*[\"'](?<id>[^\"']+)[\"'](?:(?!\\n\\s*\\}).)*?\\}", RegexOptions.Compiled | RegexOptions.Singleline);

    public async Task<MapControlCatalog> ResolveAsync(string? version, string expansion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("Choose a Factorio version before resolving the map-control catalog.");
        var versionRoot = Path.Combine(paths.Versions, version);
        var baseFile = Path.Combine(versionRoot, "data", "base", "prototypes", "autoplace-controls.lua");
        if (!File.Exists(baseFile)) throw new InvalidOperationException($"Map-control declarations are unavailable for Factorio version {version}.");
        var expansionEnabled = string.Equals(expansion, "space-age", StringComparison.OrdinalIgnoreCase);
        var files = new List<string> { baseFile };
        var spaceAgeFile = Path.Combine(versionRoot, "data", "space-age", "prototypes", "autoplace-controls.lua");
        if (expansionEnabled)
        {
            var enabled = await EffectiveModsAsync(version, ct);
            if (enabled.Contains("space-age") && !File.Exists(spaceAgeFile)) throw new InvalidOperationException($"Space Age map-control declarations are unavailable for Factorio version {version}.");
            if (enabled.Contains("space-age")) files.Add(spaceAgeFile);
        }
        var entries = files.SelectMany(Parse).Where(x => SurfaceManifest.ContainsKey(x.Id)).Select(x => x with { Surface = SurfaceManifest[x.Id] })
            .GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).OrderBy(x => Array.IndexOf(SupportedSurfaces, x.Surface)).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", files.Select(File.ReadAllText)) + "\n" + string.Join(',', (await EffectiveModsAsync(version, ct)).Order()))));
        return new(version, fingerprint, SupportedSurfaces, entries);
    }

    public async Task<MapControlCatalog> ResolveForSettingsAsync(ServerSettings settings, CancellationToken ct = default) => await ResolveAsync(settings.ActiveVersion, settings.Expansion, ct);

    private async Task<HashSet<string>> EffectiveModsAsync(string version, CancellationToken ct)
    {
        var runtime = Path.Combine(paths.Mods, "mod-list.json");
        var bundled = Path.Combine(paths.Versions, version, "mods", "mod-list.json");
        var file = File.Exists(runtime) ? runtime : bundled;
        if (!File.Exists(file)) return ["base"];
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
        return doc.RootElement.GetProperty("mods").EnumerateArray().Where(x => x.GetProperty("enabled").GetBoolean()).Select(x => x.GetProperty("name").GetString() ?? "").Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<MapControlCatalogEntry> Parse(string file)
    {
        foreach (Match match in Block.Matches(File.ReadAllText(file)))
        {
            var block = match.Value; var id = match.Groups["id"].Value;
            if (!Regex.IsMatch(block, "\\btype\\s*=\\s*[\\\"']autoplace-control[\\\"']")) continue;
            var category = Regex.Match(block, "category\\s*=\\s*[\\\"'](?<v>[^\\\"']+)").Groups["v"].Value;
            if (string.IsNullOrWhiteSpace(category)) category = "resource";
            var richness = Regex.IsMatch(block, "\\brichness\\s*=\\s*true\\b");
            var canDisable = !Regex.IsMatch(block, "can_be_disabled\\s*=\\s*false\\b");
            yield return new(id, "", category, richness, canDisable);
        }
    }

    public static MapGenerationSettings Normalize(MapGenerationSettings source, MapControlCatalog catalog)
    {
        var knownIds = catalog.Controls.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var values = (source.ControlOverrides ?? [])
            .Where(x => knownIds.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        foreach (var entry in catalog.Controls)
        {
            if (!values.ContainsKey(entry.Id)) values[entry.Id] = LegacyValue(source, entry.Id);
            values[entry.Id] = Sanitize(values[entry.Id], entry);
        }
        return source with { ControlOverrides = values.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value!, StringComparer.Ordinal) };
    }

    private static MapControlOverride LegacyValue(MapGenerationSettings s, string id) => id switch
    {
        "iron-ore" => new(s.IronOre.Frequency, s.IronOre.Size, s.IronOre.Richness), "copper-ore" => new(s.CopperOre.Frequency, s.CopperOre.Size, s.CopperOre.Richness), "stone" => new(s.Stone.Frequency, s.Stone.Size, s.Stone.Richness), "coal" => new(s.Coal.Frequency, s.Coal.Size, s.Coal.Richness), "uranium-ore" => new(s.UraniumOre.Frequency, s.UraniumOre.Size, s.UraniumOre.Richness), "crude-oil" => new(s.CrudeOil.Frequency, s.CrudeOil.Size, s.CrudeOil.Richness), "trees" => new(s.Trees.Frequency, s.Trees.Size), "enemy-base" => new(s.EnemyBase.Frequency, s.EnemyBase.Size, s.EnemyBase.Richness), _ => new()
    };

    private static MapControlOverride Sanitize(MapControlOverride value, MapControlCatalogEntry entry) => new(Level(value.Frequency), Level(value.Size), entry.Richness ? Level(value.Richness) : null);
    private static string? Level(string? value) => value is "very-low" or "low" or "normal" or "high" or "very-high" or "none" ? value : "normal";
}
