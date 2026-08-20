using FactorioManager.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"factorio-manager-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetupCodeCanOnlyConfigureOneAdminAccount()
    {
        var store = await CreateStoreAsync();
        var setup = new SetupCodeService(store);

        Assert.True(await setup.TryConfigureAsync(setup.Code, "this-is-a-safe-password"));
        Assert.True(await setup.IsConfiguredAsync());
        Assert.True(await setup.VerifyPasswordAsync("this-is-a-safe-password"));
        Assert.False(await setup.TryConfigureAsync(setup.Code, "another-safe-password"));
        Assert.False(await setup.VerifyPasswordAsync("another-safe-password"));
    }

    [Fact]
    public async Task AdminPasswordCanBeChangedWithTheCurrentPassword()
    {
        var store = await CreateStoreAsync();
        var setup = new SetupCodeService(store);

        Assert.True(await setup.TryConfigureAsync(setup.Code, "this-is-a-safe-password"));
        Assert.False(await setup.ChangePasswordAsync("wrong-password", "another-safe-password"));
        Assert.True(await setup.ChangePasswordAsync("this-is-a-safe-password", "another-safe-password"));
        Assert.True(await setup.VerifyPasswordAsync("another-safe-password"));
        Assert.False(await setup.VerifyPasswordAsync("this-is-a-safe-password"));
    }

    [Fact]
    public async Task PlayerListsArePersistentAndDeduplicateNames()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var players = new PlayerListService(paths);

        var afterAdd = await players.AddAsync("admins", "Engineer", CancellationToken.None);
        await players.AddAsync("admins", "engineer", CancellationToken.None);
        var afterRemove = await players.RemoveAsync("admins", "ENGINEER", CancellationToken.None);

        Assert.Single(afterAdd);
        Assert.Empty(await players.ListAsync("admins", CancellationToken.None));
        Assert.Empty(afterRemove);
    }

    [Fact]
    public void InvalidMapSettingsAreRejectedByValidator()
    {
        var errors = MapGenerationSettingsValidator.Validate(new MapGenerationSettings { Width = 32, ExpansionMinCooldown = 10, ExpansionMaxCooldown = 5, CliffElevationInterval = 0 });
        Assert.Contains(errors, error => error.Contains("cooldown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("Cliff", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("dimensions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GeneratedMapSettingsOmitUnsetSeedAndConvertCooldownToTicks()
    {
        var map = new MapGenerationSettings { ExpansionMinCooldown = 2, ExpansionMaxCooldown = 3 };
        using var mapGen = JsonDocument.Parse(MapGenerationSettingsJson.SerializeMapGeneration(map));
        Assert.False(mapGen.RootElement.TryGetProperty("seed", out _));
        Assert.False(mapGen.RootElement.TryGetProperty("enemy_evolution", out _));
        var cliff = mapGen.RootElement.GetProperty("cliff_settings");
        Assert.True(cliff.TryGetProperty("cliff_elevation_0", out _));
        Assert.True(cliff.TryGetProperty("cliff_elevation_interval", out _));
        Assert.Equal(JsonValueKind.Number, cliff.GetProperty("richness").ValueKind);
        Assert.Equal(0, mapGen.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(0, mapGen.RootElement.GetProperty("height").GetInt32());
        Assert.True(mapGen.RootElement.GetProperty("autoplace_controls").TryGetProperty("trees", out _));

        using var runtime = JsonDocument.Parse(MapGenerationSettingsJson.SerializeMapSettings(map));
        var expansion = runtime.RootElement.GetProperty("enemy_expansion");
        Assert.Equal(3, expansion.GetProperty("min_expansion_distance").GetInt32());
        Assert.Equal(7_200, expansion.GetProperty("min_expansion_cooldown").GetInt32());
        Assert.Equal(10_800, expansion.GetProperty("max_expansion_cooldown").GetInt32());
    }

    [Fact]
    public void SystemHealthReportsPersistentInventory()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        File.WriteAllText(paths.Database, "db");
        File.WriteAllText(Path.Combine(paths.Saves, "world.zip"), "save");
        File.WriteAllText(Path.Combine(paths.Backups, "backup.zip"), "backup");
        File.WriteAllText(Path.Combine(paths.Mods, "mod.zip"), "mod");
        Directory.CreateDirectory(Path.Combine(paths.Versions, "2.0.77"));

        var health = new SystemHealthService(paths).GetSnapshot();

        Assert.True(health.DatabasePresent);
        Assert.Equal(1, health.SaveCount);
        Assert.Equal(1, health.BackupCount);
        Assert.Equal(1, health.VersionCount);
        Assert.Equal(1, health.ModFileCount);
    }

    [Fact]
    public async Task MaintenanceStatusCalculatesNextRunsFromPersistedState()
    {
        var store = await CreateStoreAsync();
        var lastBackup = DateTimeOffset.UtcNow.AddHours(-2);
        var lastUpdate = DateTimeOffset.UtcNow.AddMinutes(-30);
        await store.SetAsync("settings", new ServerSettings(BackupIntervalHours: 6, BackupRetention: 4), CancellationToken.None);
        await store.SetAsync<DateTimeOffset?>("last_scheduled_backup", lastBackup, CancellationToken.None);
        await store.SetAsync<DateTimeOffset?>("last_update_check", lastUpdate, CancellationToken.None);

        var status = await new MaintenanceStatusService(store).GetAsync(CancellationToken.None);

        Assert.Equal(6, status.BackupIntervalHours);
        Assert.Equal(4, status.BackupRetention);
        Assert.Equal(lastBackup, status.LastScheduledBackup);
        Assert.Equal(lastBackup.AddHours(6), status.NextScheduledBackup);
        Assert.Equal(lastUpdate, status.LastUpdateCheck);
        Assert.Equal(lastUpdate.AddHours(1), status.NextUpdateCheck);
        Assert.True(status.CheckedAt >= lastUpdate);
    }

    [Fact]
    public async Task MaintenanceStatusLeavesNextRunsUnsetBeforeFirstExecution()
    {
        var store = await CreateStoreAsync();

        var status = await new MaintenanceStatusService(store).GetAsync(CancellationToken.None);

        Assert.Null(status.LastScheduledBackup);
        Assert.Null(status.NextScheduledBackup);
        Assert.Null(status.LastUpdateCheck);
        Assert.Null(status.NextUpdateCheck);
    }

    [Fact]
    public async Task BackupServiceCreatesBackupAndAppliesRetention()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetAsync("settings", new ServerSettings(ActiveSave: "world.zip", BackupRetention: 1), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(paths.Saves, "world.zip"), "save-data");

        var backups = new BackupService(paths, store);
        var first = await backups.CreateBackupAsync("manual", CancellationToken.None);
        await Task.Delay(1100);
        var second = await backups.CreateBackupAsync("manual", CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(paths.Backups, second)));
        Assert.Single(Directory.EnumerateFiles(paths.Backups, "*.zip"));
        Assert.False(File.Exists(Path.Combine(paths.Backups, first)));
    }

    private async Task<StateStore> CreateStoreAsync()
    {
        var store = new StateStore(CreatePaths());
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private DataPaths CreatePaths()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataRoot"] = _root }).Build();
        return new DataPaths(configuration);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
