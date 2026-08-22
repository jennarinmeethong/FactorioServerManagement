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
    public Dictionary<string, MapControlOverride> ControlOverrides { get; init; } = new(StringComparer.Ordinal);
}

public sealed record ServerSettings(
    string ServerName = "My Factorio Server",
    string Description = "Managed by Factorio Server Manager",
    int MaxPlayers = 0,
    bool VisibilityPublic = false,
    string? ServerPassword = null,
    int AutosaveMinutes = 10,
    int AutosaveSlots = 5,
    bool VisibilityLan = true,
    bool IgnorePlayerLimitForReturningPlayers = false,
    string AllowCommands = "admins-only",
    string[]? Tags = null,
    string? ActiveSave = null,
    string Channel = "stable",
    string? ActiveVersion = null,
    int BackupIntervalHours = 24,
    int BackupRetention = 7,
    string TimeZone = "UTC",
    MapGenerationSettings? MapGeneration = null,
    string Expansion = "vanilla",
    Dictionary<string, MapGenerationSettings>? MapGenerationProfiles = null,
    bool ScheduledRestartEnabled = false,
    string ScheduledRestartTime = "03:00",
    bool ScheduledUpdateChecksEnabled = true,
    bool AlertsEnabled = false)
{
    public MapGenerationSettings MapGeneration { get; init; } = MapGeneration ?? new();
    public string[] Tags { get; init; } = Tags ?? ["Docker", "Factorio Manager"];
    public Dictionary<string, MapGenerationSettings> MapGenerationProfiles { get; init; } =
        MapGenerationProfiles ?? new Dictionary<string, MapGenerationSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["vanilla"] = MapGeneration ?? new(),
            ["space-age"] = MapGeneration ?? new()
        };
}

public sealed record ModSettingDefinition(
    string Id,
    string ModName,
    string SettingType,
    string ValueType,
    object? Default = null,
    double? Minimum = null,
    double? Maximum = null,
    string[]? AllowedValues = null,
    bool Required = false);
public sealed record ModSettingValue(string Type, object? Value)
{
    public static ModSettingValue Boolean(bool value) => new("bool", value);
    public static ModSettingValue Integer(long value) => new("int", value);
    public static ModSettingValue Double(double value) => new("double", value);
    public static ModSettingValue String(string value) => new("string", value);
    public static ModSettingValue Enum(string value) => new("enum", value);
    public static ModSettingValue Color(string value) => new("color", value);
    public static ModSettingValue Color(double r, double g, double b, double a = 1) => new("color", new ModSettingColor(r, g, b, a));
}
public sealed record ModSettingColor(double R, double G, double B, double A);
public sealed record EnabledModCompatibility(string Name, string Version, string? Sha256);
public sealed record ModSettingsCompatibility(
    string FactorioVersion,
    string Expansion,
    string? SaveName,
    string? SaveSha256,
    EnabledModCompatibility[] EnabledMods,
    string CatalogFingerprint);
public sealed record ModSettingsArtifact(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    ModSettingsCompatibility Compatibility,
    Dictionary<string, ModSettingValue> Values);
public sealed record ModSettingsDocument(ModSettingsArtifact Artifact, ModSettingDefinition[] Definitions, string[] Diagnostics);
public sealed record ModSettingsUpdateRequest(ModSettingsArtifact Artifact, bool ConfirmStopped = false);

public sealed record MapExchangeCompatibility(
    string FactorioVersion,
    string Expansion,
    EnabledModCompatibility[] EnabledMods,
    string CatalogFingerprint,
    string InputFingerprint);
public sealed record MapExchangeImportRequest(string Exchange, string SaveName, bool ConfirmStopped = false);
public sealed record MapExchangeExportRequest(string SaveName, bool ConfirmStopped = false);
public sealed record MapExchangeImportResult(string SaveName, MapExchangeCompatibility Compatibility, string Message);
public sealed record MapExchangeExportResult(string Exchange, MapExchangeCompatibility Compatibility);
public sealed record MapPreviewRequest(MapGenerationSettings MapGeneration, string Planet = "Nauvis", int? Seed = null, int Size = 512, string Offset = "0,0", bool ConfirmStopped = false);
public sealed record MapPreviewResult(
    string Planet,
    int Seed,
    int Size,
    string ContentType,
    string Base64Png,
    long Length,
    string Sha256,
    MapExchangeCompatibility Compatibility);

public sealed record ServerStatus(ServerState State, int? ProcessId, DateTimeOffset ChangedAt, int RestartAttempt, string? Message);
public sealed record SetupRequest(string Code, string Password, string? FactorioUsername, string? FactorioToken);
public sealed record LoginRequest(string Password, string? Username = null);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record SecretSettings(
    string? FactorioUsername,
    string? FactorioToken,
    string? DiscordWebhookUrl = null,
    string? TelegramBotToken = null,
    string? TelegramChatId = null);
public sealed record NotificationSettingsRequest(
    string? DiscordWebhookUrl = null,
    string? TelegramBotToken = null,
    string? TelegramChatId = null,
    bool Enabled = false);
public sealed record NotificationSettingsStatus(
    bool Enabled,
    bool DiscordConfigured,
    bool TelegramConfigured);
public sealed record MaintenanceRunRequest(bool Confirm);
public sealed record ConfigurationBundle(
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    ServerSettings Settings,
    ModEntry[] Mods,
    ModProfile[] ModProfiles);
public sealed record ConfigurationImportRequest(ConfigurationBundle Bundle, bool Confirm);
public sealed record HealthSample(
    DateTimeOffset Timestamp,
    double CpuUsagePercent,
    long WorkingSetBytes,
    long? DataFreeBytes,
    int SaveCount,
    int BackupCount,
    int VersionCount,
    int ModFileCount);
public sealed record FactorioCredentialsRequest(string? Username = null, string? Token = null);
public enum ModSource { Portal, Local }
public sealed record ModEntry(
    string Name,
    string Version,
    bool Enabled,
    string[] Dependencies,
    DateTimeOffset InstalledAt,
    ModSource Source = ModSource.Portal,
    string? ArchiveFileName = null,
    string? Sha256 = null,
    string? FactorioVersionRequirement = null);
public sealed record ModUpdateInfo(string Name, string InstalledVersion, string? LatestVersion, bool UpdateAvailable, string[] MissingDependencies, string? DownloadUrl);
public sealed record ModInstallRequest(string Name, string? DownloadUrl = null, bool Enabled = true, string? Version = null, bool IncludeDependencies = true, bool Confirm = false);
public sealed record ModUploadRequest(bool Enabled = true, bool IncludeDependencies = true, bool Confirm = false);
public sealed record ModProfileEntry(string Name, string Version, bool Enabled);
public sealed record ModProfile(string Name, ModProfileEntry[] Mods, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ModProfileRequest(string Name, ModProfileEntry[] Mods);
public sealed record ModBulkUpdateRequest(string[] Names, bool Confirm = false);
public sealed record ModOperationResult(string Operation, bool Success, string Message, ModEntry[] Mods, string[] Blockers = null!);
public sealed record ModPreflightRequest(string Operation, string[] Names, string? ProfileName = null, bool Confirm = false);
public sealed record ModPreflightResult(string Operation, bool Allowed, bool RequiresBackup, string? ActiveSave, string? ActiveVersion, ModEntry[] PlannedMods, string[] Blockers, string[] Warnings, string[] Downloads);
public sealed record ModRecoveryStatus(bool Pending, string? Message, string[] QuarantinedFiles, DateTimeOffset? StartedAt);
public sealed record ModSaveBaseline(string SaveName, ModEntry[] Mods, DateTimeOffset CapturedAt);
public sealed record VersionApplyRequest(string Version, string Channel, bool Confirm = false);
public sealed record PlayerListRequest(string PlayerName);
public sealed record LivePlayer(string Name, int? OnlineSinceSeconds = null);
public sealed record LiveChatRequest(string Message);
public sealed record LivePlayerActionRequest(string PlayerName, string? Reason = null);
public sealed record SaveCreateRequest(string Name);
public sealed record BackupMetadata(string Id, string FileName, string SourceSave, long Length, string Sha256, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record BackupRenameRequest(string Name);
public sealed record BackupRestoreRequest(bool Confirm);
public sealed record UpdateStatus(string? AvailableVersion, string? CheckedVersion, DateTimeOffset CheckedAt, string? Error);
public enum UserRole { Owner, Admin, Viewer }
public sealed record UserRecord(string Id, string Username, UserRole Role, string SecurityStamp, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record CreateUserRequest(string Username, string Password, UserRole Role = UserRole.Viewer);
public sealed record ChangeRoleRequest(UserRole Role);
public sealed record UserPasswordRequest(string Password);
public sealed record AuditEvent(long Id, DateTimeOffset OccurredAtUtc, string? ActorUserId, string Action, string TargetType, string? TargetId, string Outcome, string DetailsJson);
