using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace FactorioManager.Api;

public sealed record RconEndpoint(int Port, string Password);

public static class LivePlayerParser
{
    // Factorio's /players output is intentionally treated as display text, never as authority for bans.
    public static IReadOnlyList<LivePlayer> ParsePlayers(string text)
    {
        var result = new List<LivePlayer>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(line, @"^(?:[-*]\s*)?(?<name>[^\s].*?)(?:\s+\([^)]*\))?$", RegexOptions.CultureInvariant);
            if (match.Success && !line.Contains("players online", StringComparison.OrdinalIgnoreCase) && !line.Contains("no players", StringComparison.OrdinalIgnoreCase))
            {
                var name = match.Groups["name"].Value.Trim();
                if (name.Length is > 0 and <= 64 && !name.Contains(':')) result.Add(new LivePlayer(name));
            }
        }
        return result.DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed class SourceRconClient
{
    private sealed record RconPacket(int Id, int Type, string Body);
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string> ExecuteAsync(RconEndpoint endpoint, string command, CancellationToken cancellationToken)
    {
        if (endpoint.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(endpoint.Password)) throw new InvalidOperationException("RCON is unavailable.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port, timeout.Token);
            await using var stream = tcp.GetStream();
            await WritePacketAsync(stream, 1, 3, endpoint.Password, timeout.Token);
            var auth = await ReadPacketAsync(stream, timeout.Token);
            if (auth.Id != 1 || auth.Type != 2 || auth.Body.Length != 0)
                throw new InvalidOperationException("RCON authentication failed.");
            await WritePacketAsync(stream, 2, 2, command, timeout.Token);
            var response = await ReadPacketAsync(stream, timeout.Token);
            if (response.Id != 2 || response.Type != 0) throw new InvalidOperationException("Invalid RCON command response.");
            if (IsCommandError(response.Body)) throw new InvalidOperationException("Factorio rejected the RCON command.");
            return response.Body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("RCON request timed out."); }
        catch (SocketException exception) { throw new RconUnavailableException("RCON is unavailable.", exception); }
        catch (IOException exception) { throw new RconUnavailableException("RCON is unavailable.", exception); }
        finally { gate.Release(); }
    }

    private static async Task WritePacketAsync(NetworkStream stream, int id, int type, string body, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body + "\0\0");
        var length = 4 + 4 + payload.Length;
        var packet = new byte[4 + length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        payload.CopyTo(packet.AsSpan(12));
        await stream.WriteAsync(packet, ct);
    }
    private static bool IsCommandError(string body) => body.Contains("unknown command", StringComparison.OrdinalIgnoreCase) || body.Contains("error", StringComparison.OrdinalIgnoreCase) || body.Contains("failed", StringComparison.OrdinalIgnoreCase) || body.Contains("cannot", StringComparison.OrdinalIgnoreCase) || body.Contains("can't", StringComparison.OrdinalIgnoreCase);
    private static async Task<RconPacket> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4]; await ReadExactAsync(stream, header, ct); var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 10 || length > 1_048_576) throw new InvalidOperationException("Invalid RCON response.");
        var data = new byte[length]; await ReadExactAsync(stream, data, ct);
        return new RconPacket(BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)), Encoding.UTF8.GetString(data, 8, length - 10));
    }
    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    { var read = 0; while (read < buffer.Length) { var n = await stream.ReadAsync(buffer.AsMemory(read), ct); if (n == 0) throw new IOException("RCON connection closed."); read += n; } }
}

public sealed class LivePlayerService(SourceRconClient rcon, ServerSupervisor supervisor)
{
    private static readonly HashSet<string> Reasons = ["Cheating", "Griefing", "Harassment", "Other"];
    public async Task<IReadOnlyList<LivePlayer>> ListAsync(CancellationToken ct)
    {
        EnsureRunning(); var endpoint = supervisor.RconEndpoint ?? throw new InvalidOperationException("RCON is unavailable.");
        return LivePlayerParser.ParsePlayers(await rcon.ExecuteAsync(endpoint, "/players", ct));
    }
    public async Task ChatAsync(string message, CancellationToken ct) { ValidateMessage(message); await ExecuteAsync($"/shout {message.Trim()}", ct); }
    public async Task KickAsync(string name, CancellationToken ct) { ValidateName(name); await ExecuteAsync($"/kick {name.Trim()}", ct); }
    public async Task BanAsync(string name, string? reason, CancellationToken ct) { ValidateName(name); if (reason is null || !Reasons.Contains(reason)) throw new ArgumentException("Choose a supported ban reason."); await ExecuteAsync($"/ban {name.Trim()} {reason}", ct); }
    private async Task ExecuteAsync(string command, CancellationToken ct) { EnsureRunning(); var endpoint = supervisor.RconEndpoint ?? throw new InvalidOperationException("RCON is unavailable."); await rcon.ExecuteAsync(endpoint, command, ct); }
    private void EnsureRunning() { if (!supervisor.IsRunning) throw new ServerNotRunningException(); if (supervisor.RconEndpoint is null) throw new InvalidOperationException("RCON is unavailable."); }
    public static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl) || name.Contains(' ')) throw new ArgumentException("Invalid player name."); }
    public static void ValidateMessage(string message) { if (string.IsNullOrWhiteSpace(message) || message.Length > 200 || message.Any(char.IsControl)) throw new ArgumentException("Message must be 1-200 characters."); }
}
public sealed class ServerNotRunningException : InvalidOperationException { public ServerNotRunningException() : base("The Factorio server is not running.") { } }
public sealed class RconUnavailableException(string message, Exception innerException) : InvalidOperationException(message, innerException);
