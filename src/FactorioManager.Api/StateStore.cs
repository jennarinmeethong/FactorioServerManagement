using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FactorioManager.Api;

public sealed class StateStore(DataPaths paths)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS app_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (await GetAsync<ServerSettings>("settings", cancellationToken) is null)
            await SetAsync("settings", new ServerSettings(), cancellationToken);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return value is null ? default : JsonSerializer.Deserialize<T>(value, _json);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_state(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, _json));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection Open() => new($"Data Source={paths.Database};Cache=Shared");
}
