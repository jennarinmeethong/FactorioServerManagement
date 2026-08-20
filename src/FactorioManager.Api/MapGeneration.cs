using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactorioManager.Api;

/// <summary>Validates the values exposed by the New Game map settings form.</summary>
public static class MapGenerationSettingsValidator
{
    private static readonly string[] Levels = ["very-low", "low", "normal", "high", "very-high", "none"];

    public static IReadOnlyList<string> Validate(MapGenerationSettings? map)
    {
        map ??= new MapGenerationSettings();
        var resources = new[] { map.IronOre, map.CopperOre, map.Stone, map.Coal, map.UraniumOre, map.CrudeOil, map.EnemyBase };
        var errors = new List<string>();
        if (map.Width < 0 || map.Height < 0 || map.Width > 1_000_000 || map.Height > 1_000_000 || (map.Width is > 0 and < 64) || (map.Height is > 0 and < 64)) errors.Add("Map dimensions must be 0 (infinite) or between 64 and 1,000,000 tiles.");
        if (!Levels.Contains(map.Water) || !Levels.Contains(map.StartingArea) || !Levels.Contains(map.TerrainSegmentation) || !Levels.Contains(map.CliffRichness)) errors.Add("Terrain levels are invalid.");
        if (resources.Any(resource => resource is null || !Levels.Contains(resource.Frequency) || !Levels.Contains(resource.Size) || !Levels.Contains(resource.Richness))) errors.Add("Resource levels are invalid.");
        if (map.EvolutionTime is < 0 or > 100 || map.EvolutionPollution is < 0 or > 100 || map.EvolutionDestroy is < 0 or > 100) errors.Add("Evolution factors must be between 0 and 100.");
        if (map.EnemyExpansionMinDistance < 0 || map.EnemyExpansionMaxDistance < map.EnemyExpansionMinDistance) errors.Add("Enemy expansion distances are invalid.");
        if (map.SettlerGroupMinSize < 1 || map.SettlerGroupMaxSize < map.SettlerGroupMinSize) errors.Add("Settler group sizes are invalid.");
        if (map.ExpansionMinCooldown < 1 || map.ExpansionMaxCooldown < map.ExpansionMinCooldown) errors.Add("Enemy expansion cooldowns are invalid.");
        if (map.CliffElevationInterval < 1) errors.Add("Cliff elevation interval must be positive.");
        return errors;
    }
}

public static class MapGenerationSettingsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Returns only fields accepted by Factorio's --map-gen-settings file.</summary>
    public static string SerializeMapGeneration(MapGenerationSettings source) => JsonSerializer.Serialize(new
    {
            seed = source.Seed,
            width = source.Width,
            height = source.Height,
        water = source.Water,
        starting_area = source.StartingArea,
        terrain_segmentation = source.TerrainSegmentation,
        peaceful_mode = source.PeacefulMode,
        autoplace_controls = new
        {
            coal = Resource(source.Coal), stone = Resource(source.Stone), copper_ore = Resource(source.CopperOre), iron_ore = Resource(source.IronOre), uranium_ore = Resource(source.UraniumOre), crude_oil = Resource(source.CrudeOil), trees = new { frequency = source.Trees.Frequency, size = source.Trees.Size }, enemy_base = Resource(source.EnemyBase)
        },
        cliff_settings = new { name = "cliff", cliff_elevation_0 = source.CliffElevationOffset, cliff_elevation_interval = source.CliffElevationInterval, richness = CliffRichness(source.CliffRichness) }
    }, Options);

    /// <summary>Returns the enemy evolution/expansion portion for Factorio's --map-settings file.</summary>
    public static string SerializeMapSettings(MapGenerationSettings source) => JsonSerializer.Serialize(new
    {
        enemy_evolution = new { enabled = true, time_factor = source.EvolutionTime / 100 * 0.000004, pollution_factor = source.EvolutionPollution / 100 * 0.0000009, destroy_factor = source.EvolutionDestroy / 100 * 0.002 },
        enemy_expansion = new { enabled = source.EnemyExpansionEnabled, min_expansion_distance = source.EnemyExpansionMinDistance, max_expansion_distance = source.EnemyExpansionMaxDistance, settler_group_min_size = source.SettlerGroupMinSize, settler_group_max_size = source.SettlerGroupMaxSize, min_expansion_cooldown = source.ExpansionMinCooldown * 60 * 60, max_expansion_cooldown = source.ExpansionMaxCooldown * 60 * 60 }
    }, Options);

    private static object Resource(ResourceGenerationSettings? value) => new { frequency = value?.Frequency ?? "normal", size = value?.Size ?? "normal", richness = value?.Richness ?? "normal" };

    private static double CliffRichness(string value) => value switch
    {
        "none" => 0,
        "very-low" => 0.25,
        "low" => 0.5,
        "high" => 2,
        "very-high" => 3,
        _ => 1
    };
}
