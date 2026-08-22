using FactorioManager.Api;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Buffers.Binary;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class NativeRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"factorio-manager-native-{Guid.NewGuid():N}");
    private readonly DataPaths _paths;

    public NativeRuntimeTests()
    {
        _paths = new DataPaths(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataRoot"] = _root }).Build());
        _paths.EnsureCreated();
    }

    [Fact]
    public void ReadinessFailsClosedWhenExecutableAndPrototypesAreMissing()
    {
        var result = new NativeRuntimeDetector(_paths).Check("2.0.0", "space-age");

        Assert.False(result.Ready);
        Assert.Contains("executable", result.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data/base/prototypes", result.Failure!, StringComparison.Ordinal);
        Assert.Contains("data/space-age/prototypes", result.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobDirectoryIsIsolatedAndDisposable()
    {
        await using var first = NativeJobDirectory.Create(_paths);
        await using var second = NativeJobDirectory.Create(_paths);
        Assert.NotEqual(first.Path, second.Path);
        Assert.DoesNotContain(_paths.Saves, first.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(first.ConfigPath));
        Assert.True(Directory.Exists(first.ModsPath));
        Assert.True(Directory.Exists(second.OutputPath));
        Assert.True(Directory.Exists(second.ScriptOutputPath));
        var firstPath = first.Path;
        await first.DisposeAsync();
        Assert.False(Directory.Exists(firstPath));
        Assert.True(Directory.Exists(second.Path));
    }

    [Fact]
    public async Task PreviewEvidenceRequiresPngAndReturnsDeterministicHash()
    {
        await using var job = NativeJobDirectory.Create(_paths);
        var path = Path.Combine(job.OutputPath, "preview.png");
        await File.WriteAllBytesAsync(path, [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]);

        var verifier = new NativePreviewEvidenceVerifier();
        var first = await verifier.VerifyAsync(path, CancellationToken.None);
        var second = await verifier.VerifyAsync(path, CancellationToken.None);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(11, first.Length);
    }

    [Fact]
    public async Task TimeoutKillsAndAwaitsNativeProcess()
    {
        var process = new FakeNativeProcess();
        var runner = new NativeProcessRunner(new FakeNativeProcessFactory(process));
        var request = new NativeProcessRequest("fake", [], _root, TimeSpan.FromMilliseconds(25));

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(process.KillCalled);
        Assert.True(process.UncancellableWaitCompleted);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CleanupTimeoutFailsDeterministicallyWhenKilledProcessWillNotExit()
    {
        var process = new FakeNativeProcess(completeOnKill: false);
        var runner = new NativeProcessRunner(new FakeNativeProcessFactory(process), TimeSpan.FromMilliseconds(25));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new NativeProcessRequest("fake", [], _root, TimeSpan.FromMilliseconds(25)), CancellationToken.None));

        Assert.Contains("cleanup timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(process.KillCalled);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CallerCancellationKillsAwaitsAndRethrowsAfterNativeProcessCleanup()
    {
        var process = new FakeNativeProcess();
        var runner = new NativeProcessRunner(new FakeNativeProcessFactory(process));
        using var cancellation = new CancellationTokenSource();
        var request = new NativeProcessRequest("fake", [], _root, TimeSpan.FromMinutes(1));
        var run = runner.RunAsync(request, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(process.KillCalled);
        Assert.True(process.UncancellableWaitCompleted);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task NativeOutputCaptureDrainsAndBoundsDiagnostics()
    {
        var process = new FakeNativeProcess(new string('o', 70 * 1024), new string('e', 70 * 1024));
        var runner = new NativeProcessRunner(new FakeNativeProcessFactory(process));

        var result = await runner.RunAsync(new NativeProcessRequest("fake", [], _root, TimeSpan.FromMilliseconds(25)), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Contains("[output truncated]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[output truncated]", result.StandardError, StringComparison.Ordinal);
        Assert.True(process.Disposed);
    }

    private sealed class FakeNativeProcessFactory(FakeNativeProcess process) : INativeProcessFactory
    {
        public INativeProcess Start(ProcessStartInfo startInfo) => process;
    }

    [Fact]
    public async Task RuntimeRejectsHostIncompatibleExecutableHeader()
    {
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(_paths.Versions, "2.0.0", "bin", "x64", "factorio.exe")
            : Path.Combine(_paths.Versions, "2.0.0", "bin", "x64", "factorio");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllBytesAsync(executable, OperatingSystem.IsWindows()
            ? LinuxX64ElfHeader()
            : WindowsX64PeHeader());
        Directory.CreateDirectory(Path.Combine(_paths.Versions, "2.0.0", "data", "base", "prototypes"));

        var result = new NativeRuntimeDetector(_paths).Check("2.0.0");

        Assert.False(result.Ready);
        Assert.Contains(OperatingSystem.IsWindows() ? "Linux ELF" : "Windows PE", result.Failure!, StringComparison.Ordinal);
        Assert.Contains("requires", result.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeAcceptsHostCompatibleExecutableHeader()
    {
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(_paths.Versions, "2.0.1", "bin", "x64", "factorio.exe")
            : Path.Combine(_paths.Versions, "2.0.1", "bin", "x64", "factorio");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllBytesAsync(executable, HostX64Header());
        Directory.CreateDirectory(Path.Combine(_paths.Versions, "2.0.1", "data", "base", "prototypes"));

        var result = new NativeRuntimeDetector(_paths).Check("2.0.1");

        Assert.True(result.Ready, result.Failure);
    }

    [Fact]
    public async Task RuntimeRejectsTruncatedHostExecutableHeaderWithPreciseReadinessFailure()
    {
        await WriteRuntimeAsync("2.0.2", OperatingSystem.IsWindows() ? [(byte)'M', (byte)'Z'] : [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        var result = new NativeRuntimeDetector(_paths).Check("2.0.2");

        Assert.False(result.Ready);
        Assert.Contains("truncated", result.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OperatingSystem.IsWindows() ? "Windows PE" : "Linux ELF", result.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeRejectsCorruptHostExecutableHeaderWithPreciseReadinessFailure()
    {
        var header = HostX64Header();
        if (OperatingSystem.IsWindows()) header[0x80] = (byte)'X';
        else header[6] = 0;
        await WriteRuntimeAsync("2.0.3", header);

        var result = new NativeRuntimeDetector(_paths).Check("2.0.3");

        Assert.False(result.Ready);
        Assert.Contains("corrupt", result.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OperatingSystem.IsWindows() ? "PE signature" : "ident version", result.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeRejectsWrongArchitectureHostExecutableHeaderWithPreciseReadinessFailure()
    {
        var header = HostX64Header();
        if (OperatingSystem.IsWindows()) BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x84), 0x014c);
        else BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), 0x0003);
        await WriteRuntimeAsync("2.0.4", header);

        var result = new NativeRuntimeDetector(_paths).Check("2.0.4");

        Assert.False(result.Ready);
        Assert.Contains("not", result.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OperatingSystem.IsWindows() ? "x64" : "x86_64", result.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealLinux2077FixtureIsAcceptedByLinuxValidationAndRejectedByWindowsValidation()
    {
        var fixture = Path.Combine(FindRepositoryRoot(), "data", "versions", "2.0.77", "bin", "x64", "factorio");

        Assert.True(File.Exists(fixture), $"Expected Linux runtime fixture at '{fixture}'.");
        Assert.True(NativeRuntimeDetector.HasHostCompatibleExecutableHeader(fixture, windowsHost: false, out var linuxFormat), linuxFormat);
        Assert.False(NativeRuntimeDetector.HasHostCompatibleExecutableHeader(fixture, windowsHost: true, out var windowsFormat));
        Assert.Contains("Linux ELF", windowsFormat, StringComparison.Ordinal);

        if (OperatingSystem.IsLinux())
        {
            var fixturePaths = new DataPaths(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataRoot"] = Path.Combine(FindRepositoryRoot(), "data")
            }).Build());
            var readiness = new NativeRuntimeDetector(fixturePaths).Check("2.0.77");
            Assert.True(readiness.Ready, readiness.Failure);
        }
    }

    private async Task WriteRuntimeAsync(string version, byte[] header)
    {
        var executable = Path.Combine(_paths.Versions, version, "bin", "x64", OperatingSystem.IsWindows() ? "factorio.exe" : "factorio");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllBytesAsync(executable, header);
        Directory.CreateDirectory(Path.Combine(_paths.Versions, version, "data", "base", "prototypes"));
    }

    private static byte[] HostX64Header() => OperatingSystem.IsWindows() ? WindowsX64PeHeader() : LinuxX64ElfHeader();

    private static byte[] WindowsX64PeHeader()
    {
        var header = new byte[0x86];
        header[0] = (byte)'M';
        header[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x3c), 0x80);
        header[0x80] = (byte)'P';
        header[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x84), 0x8664);
        return header;
    }

    private static byte[] LinuxX64ElfHeader()
    {
        var header = new byte[20];
        header[0] = 0x7f;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = 2;
        header[5] = 1;
        header[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), 0x003e);
        return header;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FactorioServerManager.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private sealed class FakeNativeProcess : INativeProcess
    {
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool completeOnKill;
        public FakeNativeProcess(string standardOutput = "safe output", string standardError = "safe error", bool completeOnKill = true)
        {
            this.completeOnKill = completeOnKill;
            StandardOutput = new(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(standardOutput)));
            StandardError = new(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(standardError)));
        }

        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }
        public bool HasExited { get; private set; }
        public int ExitCode => 0;
        public bool KillCalled { get; private set; }
        public bool UncancellableWaitCompleted { get; private set; }
        public bool Disposed { get; private set; }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            if (completeOnKill)
            {
                HasExited = true;
                exited.TrySetResult();
            }
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await exited.Task.WaitAsync(cancellationToken);
            UncancellableWaitCompleted = true;
        }

        public void Dispose() => Disposed = true;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
