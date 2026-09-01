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
        if (map.TechnologyPriceMultiplier is <= 0 or > 1000) errors.Add("Technology price multiplier must be between 0 and 1000.");
        if (map.SpoilTimeModifier is <= 0 or > 1000) errors.Add("Spoil time modifier must be between 0 and 1000.");
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
        autoplace_controls = new Dictionary<string, object>
        {
            ["iron-ore"] = Resource(source.IronOre),
            ["copper-ore"] = Resource(source.CopperOre),
            ["stone"] = Resource(source.Stone),
            ["coal"] = Resource(source.Coal),
            ["uranium-ore"] = Resource(source.UraniumOre),
            ["crude-oil"] = Resource(source.CrudeOil),
            ["trees"] = new { frequency = source.Trees.Frequency, size = source.Trees.Size },
            ["enemy-base"] = Resource(source.EnemyBase)
        },
        cliff_settings = new { name = "cliff", cliff_elevation_0 = source.CliffElevationOffset, cliff_elevation_interval = source.CliffElevationInterval, richness = CliffRichness(source.CliffRichness) }
    }, Options);

    public static string SerializeMapGeneration(MapGenerationSettings source, MapControlCatalog catalog) => JsonSerializer.Serialize(new
    {
        seed = source.Seed, width = source.Width, height = source.Height, water = source.Water, starting_area = source.StartingArea,
        terrain_segmentation = source.TerrainSegmentation, peaceful_mode = source.PeacefulMode,
        autoplace_controls = catalog.Controls.ToDictionary(x => x.Id, x => (object)Control(source.ControlOverrides.TryGetValue(x.Id, out var value) ? value : new MapControlOverride(), x)),
        cliff_settings = new { name = "cliff", cliff_elevation_0 = source.CliffElevationOffset, cliff_elevation_interval = source.CliffElevationInterval, richness = CliffRichness(source.CliffRichness) }
    }, Options);

    /// <summary>Returns a complete Factorio --map-settings file, preserving the map controls exposed by this manager.</summary>
    public static string SerializeMapSettings(MapGenerationSettings source) => JsonSerializer.Serialize(new
    {
        difficulty_settings = new { technology_price_multiplier = source.TechnologyPriceMultiplier, spoil_time_modifier = source.SpoilTimeModifier },
        pollution = new
        {
            enabled = true,
            diffusion_ratio = 0.02,
            min_to_diffuse = 15,
            ageing = 1,
            expected_max_per_chunk = 150,
            min_to_show_per_chunk = 50,
            min_pollution_to_damage_trees = 60,
            pollution_with_max_forest_damage = 150,
            pollution_per_tree_damage = 50,
            pollution_restored_per_tree_damage = 10,
            max_pollution_to_restore_trees = 20,
            enemy_attack_pollution_consumption_modifier = 1
        },
        enemy_evolution = new { enabled = true, time_factor = source.EvolutionTime / 100 * 0.000004, pollution_factor = source.EvolutionPollution / 100 * 0.0000009, destroy_factor = source.EvolutionDestroy / 100 * 0.002 },
        enemy_expansion = new
        {
            enabled = source.EnemyExpansionEnabled,
            max_expansion_distance = source.EnemyExpansionMaxDistance,
            min_expansion_distance = source.EnemyExpansionMinDistance,
            friendly_base_influence_radius = 6,
            enemy_building_influence_radius = 3,
            building_coefficient = 0.5,
            other_base_coefficient = 3.0,
            neighbouring_chunk_coefficient = 0.5,
            neighbouring_base_chunk_coefficient = 0.5,
            max_colliding_tiles_coefficient = 0.8,
            settler_group_min_size = source.SettlerGroupMinSize,
            settler_group_max_size = source.SettlerGroupMaxSize,
            evolution_group_size_factor = 4.0,
            min_expansion_cooldown = source.ExpansionMinCooldown * 60 * 60,
            max_expansion_cooldown = source.ExpansionMaxCooldown * 60 * 60
        },
        unit_group = new
        {
            min_group_gathering_time = 3600,
            max_group_gathering_time = 36000,
            max_wait_time_for_late_members = 7200,
            max_group_radius = 30.0,
            min_group_radius = 5.0,
            max_member_speedup_when_behind = 1.4,
            max_member_slowdown_when_ahead = 0.6,
            max_group_slowdown_factor = 0.3,
            max_group_member_fallback_factor = 3,
            member_disown_distance = 10,
            tick_tolerance_when_member_arrives = 60,
            max_gathering_unit_groups = 30,
            max_unit_group_size = 200
        },
        path_finder = new
        {
            fwd2bwd_ratio = 5,
            goal_pressure_ratio = 2,
            max_steps_worked_per_tick = 1000,
            max_work_done_per_tick = 8000,
            use_path_cache = true,
            short_cache_size = 5,
            long_cache_size = 25,
            short_cache_min_cacheable_distance = 10,
            short_cache_min_algo_steps_to_cache = 50,
            long_cache_min_cacheable_distance = 30,
            cache_max_connect_to_cache_steps_multiplier = 100,
            cache_accept_path_start_distance_ratio = 0.2,
            cache_accept_path_end_distance_ratio = 0.15,
            negative_cache_accept_path_start_distance_ratio = 0.3,
            negative_cache_accept_path_end_distance_ratio = 0.3,
            cache_path_start_distance_rating_multiplier = 10,
            cache_path_end_distance_rating_multiplier = 20,
            stale_enemy_with_same_destination_collision_penalty = 30,
            ignore_moving_enemy_collision_distance = 5,
            enemy_with_different_destination_collision_penalty = 30,
            general_entity_collision_penalty = 10,
            general_entity_subsequent_collision_penalty = 3,
            extended_collision_penalty = 3,
            max_clients_to_accept_any_new_request = 10,
            max_clients_to_accept_short_new_request = 100,
            direct_distance_to_consider_short_request = 100,
            short_request_max_steps = 1000,
            short_request_ratio = 0.5,
            min_steps_to_check_path_find_termination = 2000,
            start_to_goal_cost_multiplier_to_terminate_path_find = 2000.0,
            overload_levels = new[] { 0, 100, 500 },
            overload_multipliers = new[] { 2, 3, 4 },
            negative_path_cache_delay_interval = 20
        },
        asteroids = new { spawning_rate = 1, max_ray_portals_expanded_per_tick = 100 },
        max_failed_behavior_count = 3
    }, Options);

    private static object Resource(ResourceGenerationSettings? value) => new { frequency = value?.Frequency ?? "normal", size = value?.Size ?? "normal", richness = value?.Richness ?? "normal" };
    private static object Control(MapControlOverride value, MapControlCatalogEntry entry) => entry.Richness
        ? new { frequency = value.Frequency ?? "normal", size = value.Size ?? "normal", richness = value.Richness ?? "normal" }
        : new { frequency = value.Frequency ?? "normal", size = value.Size ?? "normal" };

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
