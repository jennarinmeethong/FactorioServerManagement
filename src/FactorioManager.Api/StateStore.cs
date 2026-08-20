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
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS users (id TEXT PRIMARY KEY, username TEXT NOT NULL COLLATE NOCASE UNIQUE, password_hash TEXT NOT NULL,
              role TEXT NOT NULL CHECK(role IN ('owner','admin','viewer')), security_stamp TEXT NOT NULL,
              created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit_events (id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at_utc TEXT NOT NULL,
              actor_user_id TEXT NULL, action TEXT NOT NULL, target_type TEXT NOT NULL, target_id TEXT NULL,
              outcome TEXT NOT NULL CHECK(outcome IN ('success','failure','denied')), details_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_audit_events_occurred ON audit_events(id DESC);
            CREATE TABLE IF NOT EXISTS backup_metadata (id TEXT PRIMARY KEY, file_name TEXT NOT NULL UNIQUE, source_save TEXT NOT NULL,
              length INTEGER NOT NULL, sha256 TEXT NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await MigrateLegacyOwnerAsync(cancellationToken);
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

    public async Task SetManyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var item in values)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_state(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", item.Key);
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(item.Value, _json));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private SqliteConnection Open() => new($"Data Source={paths.Database};Cache=Shared");

    public SqliteConnection OpenConnection() => Open();

    private async Task MigrateLegacyOwnerAsync(CancellationToken ct)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM users";
        if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) != 0) return;
        var legacy = connection.CreateCommand(); legacy.CommandText = "SELECT value FROM app_state WHERE key='admin_password'";
        var hash = await legacy.ExecuteScalarAsync(ct) as string;
        if (hash is null) return;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var insert = connection.CreateCommand(); insert.CommandText = "INSERT INTO users(id,username,password_hash,role,security_stamp,created_at_utc,updated_at_utc) VALUES($id,'admin',$hash,'owner',$stamp,$now,$now)";
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); insert.Parameters.AddWithValue("$hash", hash); insert.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N")); insert.Parameters.AddWithValue("$now", now); await insert.ExecuteNonQueryAsync(ct);
        var audit = connection.CreateCommand(); audit.CommandText = "INSERT INTO audit_events(occurred_at_utc,actor_user_id,action,target_type,target_id,outcome,details_json) VALUES($now,NULL,'bootstrap','user',NULL,'success','{}')"; audit.Parameters.AddWithValue("$now", now); await audit.ExecuteNonQueryAsync(ct);
    }
}
