using System.Text.Json;

namespace FactorioManager.Api;

public sealed class PlayerListService(DataPaths paths)
{
    public async Task<IReadOnlyList<string>> ListAsync(string kind, CancellationToken cancellationToken)
    {
        var path = PathFor(kind);
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<string>>(stream, cancellationToken: cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<string>> AddAsync(string kind, string playerName, CancellationToken cancellationToken)
    {
        ValidatePlayerName(playerName);
        var players = (await ListAsync(kind, cancellationToken)).Append(playerName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        await WriteAsync(kind, players, cancellationToken);
        return players;
    }

    public async Task<IReadOnlyList<string>> RemoveAsync(string kind, string playerName, CancellationToken cancellationToken)
    {
        var players = (await ListAsync(kind, cancellationToken)).Where(item => !string.Equals(item, playerName, StringComparison.OrdinalIgnoreCase)).ToArray();
        await WriteAsync(kind, players, cancellationToken);
        return players;
    }

    private async Task WriteAsync(string kind, IReadOnlyList<string> players, CancellationToken cancellationToken)
    {
        await using var file = File.Create(PathFor(kind));
        await JsonSerializer.SerializeAsync(file, players, cancellationToken: cancellationToken);
    }

    private string PathFor(string kind) => kind.ToLowerInvariant() switch
    {
        "admins" => Path.Combine(paths.Config, "admins.json"),
        "whitelist" => Path.Combine(paths.Config, "server-whitelist.json"),
        "bans" => Path.Combine(paths.Config, "server-banlist.json"),
        _ => throw new InvalidOperationException("Player list must be admins, whitelist, or bans.")
    };

    private static void ValidatePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName) || playerName.Length > 64 || playerName.Any(char.IsControl))
            throw new InvalidOperationException("The player name is invalid.");
    }
}
