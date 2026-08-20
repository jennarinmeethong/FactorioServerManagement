namespace FactorioManager.Api;

public enum ServerState { Stopped, Starting, Running, Stopping, Failed }

public sealed record ResourceGenerationSettings
{
    public string Frequency { get; init; } = "normal";
    public string Size { get; init; } = "normal";
    public string Richness { get; init; } = "normal";
}

/// <summary>All map-generation controls exposed by Factorio's New Game screen.</summary>
public sealed record MapGenerationSettings
{
    public int? Seed { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Water { get; init; } = "normal";
    public string StartingArea { get; init; } = "normal";
    public string TerrainSegmentation { get; init; } = "normal";
    public bool PeacefulMode { get; init; }
    public ResourceGenerationSettings IronOre { get; init; } = new();
    public ResourceGenerationSettings CopperOre { get; init; } = new();
    public ResourceGenerationSettings Stone { get; init; } = new();
    public ResourceGenerationSettings Coal { get; init; } = new();
    public ResourceGenerationSettings UraniumOre { get; init; } = new();
    public ResourceGenerationSettings CrudeOil { get; init; } = new();
    public ResourceGenerationSettings Trees { get; init; } = new();
    public ResourceGenerationSettings EnemyBase { get; init; } = new();
    public double EvolutionTime { get; init; } = 40;
    public double EvolutionPollution { get; init; } = 60;
    public double EvolutionDestroy { get; init; } = 30;
    public bool EnemyExpansionEnabled { get; init; } = true;
    public int EnemyExpansionMinDistance { get; init; } = 3;
    public int EnemyExpansionMaxDistance { get; init; } = 7;
    public int SettlerGroupMinSize { get; init; } = 5;
    public int SettlerGroupMaxSize { get; init; } = 20;
    public int ExpansionMinCooldown { get; init; } = 4;
    public int ExpansionMaxCooldown { get; init; } = 60;
    public int CliffElevationInterval { get; init; } = 40;
    public int CliffElevationOffset { get; init; }
    public string CliffRichness { get; init; } = "normal";
}

public sealed record ServerSettings(
    string ServerName = "My Factorio Server",
    string Description = "Managed by Factorio Server Manager",
    int MaxPlayers = 0,
    bool VisibilityPublic = false,
    string? ServerPassword = null,
    int AutosaveMinutes = 10,
    string? ActiveSave = null,
    string Channel = "stable",
    string? ActiveVersion = null,
    int BackupIntervalHours = 24,
    int BackupRetention = 7,
    string TimeZone = "UTC",
    MapGenerationSettings? MapGeneration = null)
{
    public MapGenerationSettings MapGeneration { get; init; } = MapGeneration ?? new();
}

public sealed record ServerStatus(ServerState State, int? ProcessId, DateTimeOffset ChangedAt, int RestartAttempt, string? Message);
public sealed record SetupRequest(string Code, string Password, string? FactorioUsername, string? FactorioToken);
public sealed record LoginRequest(string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record SecretSettings(string? FactorioUsername, string? FactorioToken);
public sealed record ModEntry(string Name, string Version, bool Enabled, string[] Dependencies, DateTimeOffset InstalledAt);
public sealed record ModUpdateInfo(string Name, string InstalledVersion, string? LatestVersion, bool UpdateAvailable, string[] MissingDependencies, string? DownloadUrl);
public sealed record ModInstallRequest(string Name, string DownloadUrl, bool Enabled = true);
public sealed record VersionApplyRequest(string Version, string Channel);
public sealed record PlayerListRequest(string PlayerName);
public sealed record SaveCreateRequest(string Name);
public sealed record UpdateStatus(string? AvailableVersion, string? CheckedVersion, DateTimeOffset CheckedAt, string? Error);
