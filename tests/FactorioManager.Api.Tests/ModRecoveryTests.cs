using FactorioManager.Api;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class ModRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factorio-manager-recovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task MalformedJournalIsQuarantined()
    {
        var (paths, store, service) = await CreateServiceAsync();
        var journal = Path.Combine(paths.Mods, ".mod-transaction.json");
        await File.WriteAllTextAsync(journal, "{ not valid json");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecoverForTestingAsync());

        Assert.Contains("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(journal));
        Assert.Equal("{ not valid json", await File.ReadAllTextAsync(journal + ".quarantine"));
    }

    [Fact]
    public async Task RecoveryReplaysPriorArchiveBytesAndPersistentModState()
    {
        var (paths, store, service) = await CreateServiceAsync();
        var priorMods = new[] { new ModEntry("space-exploration", "1.2.3", true, [], DateTimeOffset.Parse("2025-01-02T03:04:05Z")) };
        var priorProfiles = new[] { new ModProfile("stable", [new ModProfileEntry("space-exploration", "1.2.3", true)], DateTimeOffset.Parse("2025-01-02T03:04:05Z"), DateTimeOffset.Parse("2025-01-02T03:04:05Z")) };
        var priorArchive = new byte[] { 0, 1, 2, 250, 255 };
        var priorManifest = JsonSerializer.SerializeToUtf8Bytes(new { mods = new[] { new { name = "base", enabled = true }, new { name = "space-exploration", enabled = true } } });
        await store.SetAsync("mods", priorMods);
        await store.SetAsync("mod_profiles", priorProfiles);
        var archivePath = Path.Combine(paths.Mods, "space-exploration_1.2.3.zip");
        await File.WriteAllBytesAsync(archivePath, priorArchive);
        var journal = new
        {
            operation = "install",
            started = DateTimeOffset.UtcNow,
            mods = priorMods,
            profiles = priorProfiles,
            archives = new Dictionary<string, string> { ["space-exploration_1.2.3.zip"] = Convert.ToBase64String(priorArchive) },
            modList = Convert.ToBase64String(priorManifest)
        };
        await File.WriteAllTextAsync(Path.Combine(paths.Mods, ".mod-transaction.json"), JsonSerializer.Serialize(journal));

        await store.SetAsync("mods", new[] { new ModEntry("changed", "9", false, [], DateTimeOffset.UtcNow) });
        await File.WriteAllBytesAsync(archivePath, [9, 9, 9]);
        await File.WriteAllTextAsync(Path.Combine(paths.Mods, "mod-list.json"), "{\"mods\":[]}");

        await service.RecoverForTestingAsync();

        Assert.Equal(JsonSerializer.Serialize(priorMods), JsonSerializer.Serialize(await store.GetAsync<ModEntry[]>("mods")));
        Assert.Equal(JsonSerializer.Serialize(priorProfiles), JsonSerializer.Serialize(await store.GetAsync<ModProfile[]>("mod_profiles")));
        Assert.Equal(priorArchive, await File.ReadAllBytesAsync(archivePath));
        Assert.Equal(priorManifest, await File.ReadAllBytesAsync(Path.Combine(paths.Mods, "mod-list.json")));
        Assert.False(File.Exists(Path.Combine(paths.Mods, ".mod-transaction.json")));
    }

    private async Task<(DataPaths Paths, StateStore Store, ModService Service)> CreateServiceAsync()
    {
        var paths = new DataPaths(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataRoot"] = root }).Build());
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        var supervisor = new ServerSupervisor(paths, store, null!, null!);
        return (paths, store, new ModService(paths, store, null!, supervisor));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
