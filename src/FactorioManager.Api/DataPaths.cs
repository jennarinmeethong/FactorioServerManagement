namespace FactorioManager.Api;

public sealed class DataPaths
{
    public DataPaths(IConfiguration configuration)
    {
        var configuredRoot = configuration["DataRoot"] ?? "data";
        Root = Path.IsPathFullyQualified(configuredRoot)
            ? configuredRoot
            : Path.GetFullPath(configuredRoot, AppContext.BaseDirectory);
    }

    public string Root { get; }
    public string Versions => Path.Combine(Root, "versions");
    public string Saves => Path.Combine(Root, "saves");
    public string Backups => Path.Combine(Root, "backups");
    public string Mods => Path.Combine(Root, "mods");
    public string Config => Path.Combine(Root, "config");
    public string Logs => Path.Combine(Root, "logs");
    public string Database => Path.Combine(Root, "app.db");
    public string SetupCode => Path.Combine(Root, "setup-code");
    public string Secrets => Path.Combine(Config, "secrets.json");

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Root, Versions, Saves, Backups, Mods, Config, Logs })
            Directory.CreateDirectory(directory);
    }
}
