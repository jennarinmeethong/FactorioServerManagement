namespace FactorioManager.Api;

public static class ServerSettingsValidator
{
    private static readonly HashSet<string> CommandModes = ["true", "false", "admins-only"];
    private static readonly HashSet<string> ExpansionModes = ["vanilla", "space-age"];

    public static Dictionary<string, string[]> Validate(ServerSettings settings)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        AddIf(errors, "serverName", string.IsNullOrWhiteSpace(settings.ServerName), "Server name is required.");
        AddIf(errors, "serverName", settings.ServerName?.Length > 100, "Server name must be 100 characters or fewer.");
        AddIf(errors, "description", settings.Description?.Length > 1000, "Description must be 1000 characters or fewer.");
        AddIf(errors, "serverPassword", settings.ServerPassword?.Length > 100, "Server password must be 100 characters or fewer.");
        AddIf(errors, "maxPlayers", settings.MaxPlayers is < 0 or > 500, "Max players must be between 0 and 500.");
        AddIf(errors, "autosaveMinutes", settings.AutosaveMinutes is < 1 or > 120, "Autosave interval must be between 1 and 120 minutes.");
        AddIf(errors, "autosaveSlots", settings.AutosaveSlots is < 1 or > 100, "Autosave slots must be between 1 and 100.");
        AddIf(errors, "backupIntervalHours", settings.BackupIntervalHours is < 1 or > 8760, "Backup interval must be between 1 and 8760 hours.");
        AddIf(errors, "backupRetention", settings.BackupRetention is < 1 or > 365, "Backup retention must be between 1 and 365.");
        AddIf(errors, "scheduledRestartTime", !TimeOnly.TryParseExact(settings.ScheduledRestartTime, "HH:mm", out _), "Scheduled restart time must use HH:mm.");
        AddIf(errors, "allowCommands", !CommandModes.Contains(settings.AllowCommands), "Allow commands must be true, false, or admins-only.");
        AddIf(errors, "expansion", !ExpansionModes.Contains(settings.Expansion), "The game expansion profile is invalid.");
        var tags = settings.Tags ?? [];
        AddIf(errors, "tags", tags.Length > 20, "Use 20 tags or fewer.");
        AddIf(errors, "tags", tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 32), "Each tag must be between 1 and 32 characters.");

        foreach (var profile in settings.MapGenerationProfiles ?? [])
        {
            var mapErrors = MapGenerationSettingsValidator.Validate(profile.Value);
            foreach (var mapError in mapErrors)
                Add(errors, $"mapGenerationProfiles.{profile.Key}", mapError);
        }

        return errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AddIf(Dictionary<string, List<string>> errors, string key, bool condition, string message)
    {
        if (condition) Add(errors, key, message);
    }

    private static void Add(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var messages)) errors[key] = messages = [];
        if (!messages.Contains(message, StringComparer.Ordinal)) messages.Add(message);
    }
}
