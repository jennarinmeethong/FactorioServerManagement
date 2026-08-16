namespace FactorioManager.Api;

public enum ServerState { Stopped, Starting, Running, Stopping, Failed }

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
    string TimeZone = "UTC");

public sealed record ServerStatus(ServerState State, int? ProcessId, DateTimeOffset ChangedAt, int RestartAttempt, string? Message);
public sealed record SetupRequest(string Code, string Password, string? FactorioUsername, string? FactorioToken);
public sealed record LoginRequest(string Password);
public sealed record SecretSettings(string? FactorioUsername, string? FactorioToken);
public sealed record ModEntry(string Name, string Version, bool Enabled, string[] Dependencies, DateTimeOffset InstalledAt);
public sealed record ModInstallRequest(string Name, string DownloadUrl, bool Enabled = true);
public sealed record VersionApplyRequest(string Version, string Channel);
public sealed record PlayerListRequest(string PlayerName);
public sealed record SaveCreateRequest(string Name);
public sealed record UpdateStatus(string? AvailableVersion, string? CheckedVersion, DateTimeOffset CheckedAt, string? Error);
