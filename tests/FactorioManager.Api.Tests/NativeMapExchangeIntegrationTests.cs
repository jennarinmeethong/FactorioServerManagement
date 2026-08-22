using System.Text.Json;
using FactorioManager.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FactorioManager.Api.Tests;

/// <summary>Opt-in native coverage.  CI must provide an already-installed runtime; this test never downloads one.</summary>
public sealed class NativeMapExchangeIntegrationTests
{
    [Fact]
    public async Task LinuxInstalledRuntimeExportsAndImportsWithoutChangingTheSourceOrActiveSave()
    {
        var executable = Environment.GetEnvironmentVariable("FACTORIO_NATIVE_EXECUTABLE");
        var data = Environment.GetEnvironmentVariable("FACTORIO_NATIVE_DATA");
        var spaceAge = Environment.GetEnvironmentVariable("FACTORIO_NATIVE_SPACE_AGE");
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(data)
            || !string.Equals(spaceAge, "1", StringComparison.Ordinal) || !File.Exists(executable) || !Directory.Exists(data)) return;

        var root = Path.Combine(Path.GetTempPath(), $"factorio-native-roundtrip-{Guid.NewGuid():N}");
        try
        {
            var paths = new DataPaths(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataRoot"] = root }).Build());
            paths.EnsureCreated();
            var versionRoot = Path.Combine(paths.Versions, "installed");
            var linkedExecutable = Path.Combine(versionRoot, "bin", "x64", "factorio");
            Directory.CreateDirectory(Path.GetDirectoryName(linkedExecutable)!);
            File.CreateSymbolicLink(linkedExecutable, executable);
            Directory.CreateSymbolicLink(Path.Combine(versionRoot, "data"), data);

            await CreateSourceSaveAsync(paths, executable, data);
            var source = Path.Combine(paths.Saves, "source.zip");
            var sourceHash = await HashFileAsync(source);
            var store = new StateStore(paths);
            await store.InitializeAsync(CancellationToken.None);
            await store.SetAsync("settings", new ServerSettings(ActiveSave: "source.zip", ActiveVersion: "installed"), CancellationToken.None);
            var supervisor = new ServerSupervisor(paths, store, null!, NullLogger<ServerSupervisor>.Instance);
            var service = new NativeMapExchangeService(paths, store, supervisor, new MapControlCatalogService(paths), new NativeRuntimeDetector(paths), new NativeProcessRunner(), new SourceRconClient());

            var exported = await service.ExportAsync(new("source.zip", true), CancellationToken.None);
            var imported = await service.ImportAsync(new(exported.Exchange, "imported.zip", true), CancellationToken.None);

            Assert.Equal("imported.zip", imported.SaveName);
            Assert.NotEmpty(exported.Exchange);
            Assert.Equal(sourceHash, await HashFileAsync(source));
            Assert.Equal("source.zip", (await store.GetAsync<ServerSettings>("settings", CancellationToken.None))!.ActiveSave);
            Assert.True(File.Exists(Path.Combine(paths.Saves, "imported.zip")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstalledRuntimeGeneratesNauvisAndSpaceAgeVulcanusPreviews()
    {
        var executable = Environment.GetEnvironmentVariable("FACTORIO_NATIVE_EXECUTABLE");
        var data = Environment.GetEnvironmentVariable("FACTORIO_NATIVE_DATA");
        var spaceAge = Environment.GetEnvironmentVariable("FACTORIO_NATIVE_SPACE_AGE");
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(data) || !string.Equals(spaceAge, "1", StringComparison.Ordinal) || !File.Exists(executable) || !Directory.Exists(data)) return;

        var root = Path.Combine(Path.GetTempPath(), $"factorio-native-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var settings = new { seed = 12345, width = 0, height = 0 };
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "config.ini"), $"[path]\nread-data={data}\nwrite-data={root}\n[other]\ncheck-updates=false\n");
            Directory.CreateDirectory(Path.Combine(root, "mods"));
            await File.WriteAllTextAsync(Path.Combine(root, "mods", "mod-list.json"), JsonSerializer.Serialize(new { mods = new[] { new { name = "base", enabled = true }, new { name = "elevated-rails", enabled = true }, new { name = "quality", enabled = true }, new { name = "space-age", enabled = true } } }));
            await File.WriteAllTextAsync(Path.Combine(root, "map-gen-settings.json"), JsonSerializer.Serialize(settings));
            var runner = new NativeProcessRunner();
            foreach (var planet in new[] { "nauvis", "vulcanus" })
            {
                var evidence = new List<NativePreviewEvidence>();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var preview = Path.Combine(root, $"{planet}-{attempt}.png");
                    var result = await runner.RunAsync(new NativeProcessRequest(executable, ["--config", Path.Combine(root, "config.ini"), "--mod-directory", Path.Combine(root, "mods"), "--threads", "1", "--generate-map-preview", preview, "--map-gen-settings", Path.Combine(root, "map-gen-settings.json"), "--map-gen-seed", "12345", "--map-preview-size", "64", "--map-preview-offset", "0,0", "--map-preview-planet", planet], root, TimeSpan.FromMinutes(2)), CancellationToken.None);
                    Assert.False(result.TimedOut);
                    Assert.Equal(0, result.ExitCode);
                    evidence.Add(await new NativePreviewEvidenceVerifier().VerifyAsync(preview, CancellationToken.None));
                }
                Assert.Equal(evidence[0].Length, evidence[1].Length);
                Assert.Equal(evidence[0].Sha256, evidence[1].Sha256);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task CreateSourceSaveAsync(DataPaths paths, string executable, string data)
    {
        var bootstrap = Path.Combine(paths.Root, "native-bootstrap");
        Directory.CreateDirectory(bootstrap);
        var config = Path.Combine(bootstrap, "config.ini");
        var mods = Path.Combine(bootstrap, "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(config, $"[path]\nread-data={data}\nwrite-data={bootstrap}\n[other]\ncheck-updates=false\n");
        await File.WriteAllTextAsync(Path.Combine(mods, "mod-list.json"), "{\"mods\":[{\"name\":\"base\",\"enabled\":true}]}");
        var result = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(executable,
            ["--config", config, "--mod-directory", mods, "--threads", "1", "--create", Path.Combine(paths.Saves, "source.zip")], bootstrap, TimeSpan.FromMinutes(2)), CancellationToken.None);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
    }
}
