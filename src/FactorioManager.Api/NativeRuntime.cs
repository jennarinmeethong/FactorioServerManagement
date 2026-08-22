using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FactorioManager.Api;

public sealed record NativeRuntimeRequirements(
    string Version,
    string Expansion,
    string ExecutablePath,
    string DataPath,
    string[] Missing);

public sealed record NativeRuntimeReadiness(bool Ready, NativeRuntimeRequirements Requirements)
{
    public string? Failure => Ready ? null : $"Factorio native runtime is unavailable: {string.Join("; ", Requirements.Missing)}";
}

/// <summary>Checks that a complete, local Factorio runtime exists before any native operation is attempted.</summary>
public sealed class NativeRuntimeDetector(DataPaths paths)
{
    public NativeRuntimeReadiness Check(string version, string expansion = "vanilla")
    {
        if (string.IsNullOrWhiteSpace(version) || Path.GetFileName(version) != version)
            return Missing(version, expansion, "a safe Factorio version is required");

        var versionRoot = Path.Combine(paths.Versions, version);
        // Never rely on ProcessStartInfo's suffix resolution: a version directory can
        // contain runtimes copied from another host.  Native map work is fail-closed
        // until the selected executable has the format required by this host.
        var unixExecutable = Path.Combine(versionRoot, "bin", "x64", "factorio");
        var windowsExecutable = unixExecutable + ".exe";
        var windowsHost = OperatingSystem.IsWindows();
        var executable = windowsHost ? windowsExecutable : unixExecutable;
        var data = Path.Combine(versionRoot, "data");
        var missing = new List<string>();
        if (!File.Exists(executable)) missing.Add($"executable '{executable}'");
        else if (!HasHostCompatibleExecutableHeader(executable, windowsHost, out var format))
            missing.Add($"executable '{executable}' is {format}; this host requires a {(windowsHost ? "Windows PE" : "Linux ELF")} executable");
        if (!Directory.Exists(Path.Combine(data, "base", "prototypes"))) missing.Add("data/base/prototypes");
        if (string.Equals(expansion, "space-age", StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(Path.Combine(data, "space-age", "prototypes")))
            missing.Add("data/space-age/prototypes");
        if (!string.Equals(expansion, "vanilla", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(expansion, "space-age", StringComparison.OrdinalIgnoreCase))
            missing.Add($"supported expansion '{expansion}'");

        var requirements = new NativeRuntimeRequirements(version, expansion, executable, data, [.. missing]);
        return new NativeRuntimeReadiness(missing.Count == 0, requirements);
    }

    private static NativeRuntimeReadiness Missing(string version, string expansion, string reason)
    {
        var requirements = new NativeRuntimeRequirements(version, expansion, string.Empty, string.Empty, [reason]);
        return new NativeRuntimeReadiness(false, requirements);
    }

    /// <summary>Validates the executable format used by a Windows or Linux x64 Factorio runtime.</summary>
    internal static bool HasHostCompatibleExecutableHeader(string path, bool windowsHost, out string format)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> prefix = stackalloc byte[2];
            if (!TryReadExactly(stream, prefix))
            {
                format = "a truncated executable header";
                return false;
            }

            var isPe = prefix.SequenceEqual(new byte[] { (byte)'M', (byte)'Z' });
            if (windowsHost)
            {
                if (isPe) return IsWindowsX64Pe(stream, out format);
            }
            else if (isPe) { format = "a Windows PE executable"; return false; }

            Span<byte> magic = stackalloc byte[4];
            prefix.CopyTo(magic);
            if (!TryReadExactly(stream, magic[2..]))
            {
                format = prefix.SequenceEqual(new byte[] { 0x7f, (byte)'E' }) ? "a truncated Linux ELF header" : "a truncated executable header";
                return false;
            }
            if (!magic.SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' })) return Unrecognized(out format);
            if (windowsHost) { format = "a Linux ELF executable"; return false; }
            return IsLinuxX64Elf(stream, magic, out format);
        }
        catch (IOException) { format = "unreadable"; return false; }
        catch (UnauthorizedAccessException) { format = "unreadable"; return false; }
        catch (NotSupportedException) { format = "unreadable"; return false; }
    }

    private static bool IsWindowsX64Pe(Stream stream, out string format)
    {
        Span<byte> dosHeader = stackalloc byte[64];
        stream.Position = 0;
        if (!TryReadExactly(stream, dosHeader))
        {
            format = "a truncated Windows PE header";
            return false;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3c..]);
        if (peOffset < 0 || peOffset > stream.Length - 6)
        {
            format = "a corrupt Windows PE header (invalid e_lfanew)";
            return false;
        }

        stream.Position = peOffset;
        Span<byte> peAndCoff = stackalloc byte[6];
        if (!TryReadExactly(stream, peAndCoff))
        {
            format = "a truncated Windows PE header";
            return false;
        }
        if (!peAndCoff[..4].SequenceEqual(new byte[] { (byte)'P', (byte)'E', 0, 0 }))
        {
            format = "a corrupt Windows PE header (missing PE signature)";
            return false;
        }
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(peAndCoff[4..]);
        if (machine != 0x8664)
        {
            format = $"a Windows PE executable for machine 0x{machine:X4}, not x64";
            return false;
        }

        format = "a valid Windows PE x64 executable";
        return true;
    }

    private static bool IsLinuxX64Elf(Stream stream, ReadOnlySpan<byte> magic, out string format)
    {
        Span<byte> header = stackalloc byte[20];
        magic.CopyTo(header);
        if (!TryReadExactly(stream, header[4..]))
        {
            format = "a truncated Linux ELF header";
            return false;
        }
        if (header[4] != 2)
        {
            format = $"a Linux ELF executable with class {header[4]}, not 64-bit";
            return false;
        }
        if (header[5] != 1)
        {
            format = $"a Linux ELF executable with data encoding {header[5]}, not little-endian";
            return false;
        }
        if (header[6] != 1)
        {
            format = $"a corrupt Linux ELF header (ident version {header[6]})";
            return false;
        }
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        if (machine != 0x003e)
        {
            format = $"a Linux ELF executable for machine 0x{machine:X4}, not x86_64";
            return false;
        }

        format = "a valid Linux ELF x86_64 executable";
        return true;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    private static bool Unrecognized(out string format)
    {
        format = "an unrecognized executable format";
        return false;
    }
}

public sealed class NativeJobDirectory : IAsyncDisposable
{
    private NativeJobDirectory(string path) => Path = path;
    public string Path { get; }
    public string ConfigPath => System.IO.Path.Combine(Path, "config");
    public string ConfigFilePath => System.IO.Path.Combine(ConfigPath, "config.ini");
    public string ModsPath => System.IO.Path.Combine(Path, "mods");
    public string SavesPath => System.IO.Path.Combine(Path, "saves");
    public string OutputPath => System.IO.Path.Combine(Path, "output");
    public string ScriptOutputPath => System.IO.Path.Combine(Path, "script-output");

    public static NativeJobDirectory Create(DataPaths paths)
    {
        var root = System.IO.Path.Combine(paths.Root, "native-jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(System.IO.Path.Combine(root, "config"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "mods"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "saves"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "output"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "script-output"));
        return new NativeJobDirectory(root);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        return ValueTask.CompletedTask;
    }
}

public sealed record NativeProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout);
public sealed record NativeProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

public interface INativeProcessRunner
{
    Task<NativeProcessResult> RunAsync(NativeProcessRequest request, CancellationToken cancellationToken);
}

public interface INativeProcess : IDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    void Kill(bool entireProcessTree);
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface INativeProcessFactory
{
    INativeProcess Start(ProcessStartInfo startInfo);
}

public sealed class NativeProcessRunner : INativeProcessRunner
{
    private const int MaxOutputCharacters = 64 * 1024;
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private readonly INativeProcessFactory processFactory;
    private readonly TimeSpan cleanupTimeout;

    public NativeProcessRunner(INativeProcessFactory? processFactory = null, TimeSpan? cleanupTimeout = null)
    {
        this.processFactory = processFactory ?? new SystemNativeProcessFactory();
        this.cleanupTimeout = cleanupTimeout.HasValue && cleanupTimeout.Value > TimeSpan.Zero ? cleanupTimeout.Value : CleanupTimeout;
    }

    public async Task<NativeProcessResult> RunAsync(NativeProcessRequest request, CancellationToken cancellationToken)
    {
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(request.Timeout), "Native jobs must have a timeout between 1 second and 5 minutes.");

        var startInfo = new ProcessStartInfo(request.Executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        using var process = processFactory.Start(startInfo);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdout = CaptureOutputAsync(process.StandardOutput);
        var stderr = CaptureOutputAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
            return new(process.ExitCode, await stdout, await stderr, false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested;
            Exception? terminationFailure = null;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) { terminationFailure = exception; }
            var (standardOutput, standardError) = await AwaitPostKillCleanupAsync(process.WaitForExitAsync, stdout, stderr, cleanupTimeout);
            if (terminationFailure is not null)
                throw new InvalidOperationException("The native process could not be terminated cleanly.", terminationFailure);
            var result = new NativeProcessResult(-1, standardOutput, standardError, timedOut);
            if (timedOut) return result;
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>Bounds post-kill process shutdown and redirected-pipe draining.</summary>
    internal static async Task<(string StandardOutput, string StandardError)> AwaitPostKillCleanupAsync(
        Func<CancellationToken, Task> waitForExit,
        Task<string> stdout,
        Task<string> stderr,
        TimeSpan? cleanupTimeout = null)
    {
        using var cleanup = new CancellationTokenSource(cleanupTimeout ?? CleanupTimeout);
        try
        {
            await Task.WhenAll(waitForExit(cleanup.Token), stdout, stderr).WaitAsync(cleanup.Token);
            return (await stdout, await stderr);
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            throw new InvalidOperationException("The native process did not exit and drain output within the cleanup timeout after termination.");
        }
    }

    /// <summary>
    /// Starts consuming a redirected stream immediately while retaining only a
    /// bounded diagnostic prefix.  Callers must await the returned task after
    /// the process exits so the anonymous pipe is fully released.
    /// </summary>
    internal static async Task<string> CaptureOutputAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(MaxOutputCharacters);
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            var remaining = MaxOutputCharacters - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(count, remaining));
            if (count > remaining) truncated = true;
        }

        return truncated ? output.Append("\n[output truncated]").ToString() : output.ToString();
    }

    private sealed class SystemNativeProcessFactory : INativeProcessFactory
    {
        public INativeProcess Start(ProcessStartInfo startInfo)
            => new SystemNativeProcess(Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Factorio native process could not be started."));
    }

    private sealed class SystemNativeProcess(Process process) : INativeProcess
    {
        public StreamReader StandardOutput => process.StandardOutput;
        public StreamReader StandardError => process.StandardError;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
}

public sealed record NativePreviewEvidence(string OutputPath, long Length, string Sha256);

public sealed class NativePreviewEvidenceVerifier
{
    public async Task<NativePreviewEvidence> VerifyAsync(string outputPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(outputPath)) throw new InvalidOperationException("The native preview output was not produced.");
        var info = new FileInfo(outputPath);
        if (info.Length == 0) throw new InvalidOperationException("The native preview output is empty.");
        await using var stream = File.OpenRead(outputPath);
        var header = new byte[8];
        if (await stream.ReadAsync(header, cancellationToken) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidOperationException("The native preview output is not a PNG.");
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new(outputPath, info.Length, Convert.ToHexString(hash));
    }
}
