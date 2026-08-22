using System.Security.Cryptography;
using System.Buffers.Binary;
using FactorioManager.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class NativeMapExchangeServiceTests : IDisposable
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factorio-map-exchange-{Guid.NewGuid():N}");
    private readonly DataPaths paths;

    public NativeMapExchangeServiceTests()
    {
        paths = new DataPaths(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataRoot"] = root }).Build());
        paths.EnsureCreated();
    }

    [Fact]
    public async Task ImportRejectsMalformedExchangeBeforeNativeWorkAndPreservesActiveSave()
    {
        var (service, store, runner) = await CreateServiceAsync(runtimeReady: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(new("bad\0exchange", "new.zip", true), CancellationToken.None));

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("active.zip", (await store.GetAsync<ServerSettings>("settings", CancellationToken.None))!.ActiveSave);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("..\\escape.zip")]
    [InlineData("world .zip")]
    [InlineData("world\t.zip")]
    [InlineData("world\u0001.zip")]
    [InlineData("CON.zip")]
    [InlineData("COM1.backup.zip")]
    public async Task ExportRejectsCrossPlatformUnsafeSaveNames(string saveName)
    {
        var (service, _, runner) = await CreateServiceAsync(runtimeReady: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(new(saveName, true), CancellationToken.None));

        Assert.Contains("safe", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task ImportCollisionDoesNotChangeActiveSaveOrRunNativeProcess()
    {
        var (service, store, runner) = await CreateServiceAsync(runtimeReady: false);
        await File.WriteAllTextAsync(Path.Combine(paths.Saves, "existing.zip"), "existing");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(new("valid-exchange", "existing.zip", true), CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("active.zip", (await store.GetAsync<ServerSettings>("settings", CancellationToken.None))!.ActiveSave);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task PreviewFailsClosedWhenRuntimeIsNotReady()
    {
        var (service, store, runner) = await CreateServiceAsync(runtimeReady: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(new(new MapGenerationSettings(), ConfirmStopped: true), CancellationToken.None));

        Assert.Contains("runtime is unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("active.zip", (await store.GetAsync<ServerSettings>("settings", CancellationToken.None))!.ActiveSave);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task PreviewRejectsUnknownRequestedControlBeforeNormalization()
    {
        var (service, _, runner) = await CreateServiceAsync(runtimeReady: true);
        var map = new MapGenerationSettings { ControlOverrides = new(StringComparer.Ordinal) { ["uninstalled-control"] = new("high", "high", "high") } };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(new(map, ConfirmStopped: true), CancellationToken.None));

        Assert.Contains("unknown map controls", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task PreviewReturnsPngEvidenceWithDeterministicLengthAndHash()
    {
        var (service, _, runner) = await CreateServiceAsync(runtimeReady: true);

        var result = await service.PreviewAsync(new(new MapGenerationSettings(), Seed: 12345, Size: 64, ConfirmStopped: true), CancellationToken.None);

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(Png.Length, result.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)), result.Sha256);
        Assert.Equal(Convert.ToBase64String(Png), result.Base64Png);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task PreviewRejectsCompatibilityThatChangesDuringNativeWork()
    {
        var (service, _, runner) = await CreateServiceAsync(runtimeReady: true);
        runner.AfterRun = () => File.AppendAllText(Path.Combine(paths.Versions, "2.0.77", "data", "base", "prototypes", "autoplace-controls.lua"), "\n-- changed while native work ran");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(new(new MapGenerationSettings(), Seed: 12345, Size: 64, ConfirmStopped: true), CancellationToken.None));

        Assert.Contains("compatibility changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, runner.Calls);
    }

    private async Task<(NativeMapExchangeService Service, StateStore Store, PreviewProcessRunner Runner)> CreateServiceAsync(bool runtimeReady)
    {
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetAsync("settings", new ServerSettings(ActiveSave: "active.zip", ActiveVersion: "2.0.77"), CancellationToken.None);
        if (runtimeReady)
        {
            var executable = Path.Combine(paths.Versions, "2.0.77", "bin", "x64", OperatingSystem.IsWindows() ? "factorio.exe" : "factorio");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllBytesAsync(executable, HostX64Header());
            var prototypes = Path.Combine(paths.Versions, "2.0.77", "data", "base", "prototypes");
            Directory.CreateDirectory(prototypes);
            await File.WriteAllTextAsync(Path.Combine(prototypes, "autoplace-controls.lua"), "data:extend({{type = 'autoplace-control', name = 'iron-ore', category = 'resource', richness = true}})");
        }

        var runner = new PreviewProcessRunner();
        var supervisor = new ServerSupervisor(paths, store, null!, NullLogger<ServerSupervisor>.Instance);
        var service = new NativeMapExchangeService(paths, store, supervisor, new MapControlCatalogService(paths), new NativeRuntimeDetector(paths), runner, new SourceRconClient());
        return (service, store, runner);
    }

    private static byte[] HostX64Header()
    {
        if (!OperatingSystem.IsWindows())
        {
            var elf = new byte[20];
            elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
            elf[4] = 2; elf[5] = 1; elf[6] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), 0x003e);
            return elf;
        }

        var pe = new byte[0x86];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3c), 0x80);
        pe[0x80] = (byte)'P'; pe[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x84), 0x8664);
        return pe;
    }

    private sealed class PreviewProcessRunner : INativeProcessRunner
    {
        public int Calls { get; private set; }
        public Action? AfterRun { get; set; }

        public Task<NativeProcessResult> RunAsync(NativeProcessRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            var index = request.Arguments.Select((argument, position) => new { argument, position })
                .Where(entry => entry.argument == "--generate-map-preview")
                .Select(entry => entry.position)
                .DefaultIfEmpty(-1)
                .Single();
            if (index >= 0) File.WriteAllBytes(request.Arguments[index + 1], Png);
            AfterRun?.Invoke();
            return Task.FromResult(new NativeProcessResult(0, string.Empty, string.Empty, false));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
