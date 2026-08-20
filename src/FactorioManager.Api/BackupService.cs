using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FactorioManager.Api;

public sealed class BackupService(DataPaths paths, StateStore store, ServerSupervisor? supervisor = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<string> CreateBackupAsync(string reason, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct); try
        {
            EnsureStopped();
            var settings = await store.GetAsync<ServerSettings>("settings", ct) ?? new ServerSettings();
            var sourceName = SafeName(settings.ActiveSave, ".zip");
            var source = Contained(paths.Saves, sourceName);
            if (!File.Exists(source)) throw new InvalidOperationException("The selected save does not exist.");
            var id = Guid.NewGuid().ToString("N"); var fileName = id + ".zip"; var destination = Contained(paths.Backups, fileName); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true)) await input.CopyToAsync(output, ct);
                File.Move(temporary, destination);
                var now = DateTimeOffset.UtcNow; var info = new FileInfo(destination);
                try { await InsertAsync(new BackupMetadata(id, fileName, sourceName, info.Length, await HashAsync(destination, ct), now, now), ct); }
                catch { if (File.Exists(destination)) File.Delete(destination); throw; }
                await PruneAsync(settings.BackupRetention, ct); return fileName;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        } finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default) { await gate.WaitAsync(ct); try { return await ReadAllAsync(ct); } finally { gate.Release(); } }
    public async Task<(BackupMetadata Metadata, Stream Content)?> OpenReadAsync(string identity, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct); try { var m = await FindAsync(identity, ct) ?? throw new InvalidOperationException("The requested backup was not found."); var path = Contained(paths.Backups, m.FileName); await ValidateAsync(m, path, ct); return (m, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true)); } finally { gate.Release(); }
    }
    public async Task RenameAsync(string identity, string name, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct); try { var m = await FindAsync(identity, ct) ?? throw new InvalidOperationException("The requested backup was not found."); var safe = SafeName(name, ".zip"); var destination = Contained(paths.Backups, safe); if (File.Exists(destination)) throw new InvalidOperationException("A backup with that name already exists."); var original = Contained(paths.Backups, m.FileName); File.Move(original, destination); try { await UpdateAsync(m with { FileName = safe, UpdatedAtUtc = DateTimeOffset.UtcNow }, ct); } catch { if (File.Exists(destination) && !File.Exists(original)) File.Move(destination, original); throw; } } finally { gate.Release(); }
    }
    public async Task DeleteAsync(string identity, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct); try { var m = await FindAsync(identity, ct) ?? throw new InvalidOperationException("The requested backup was not found."); var path = Contained(paths.Backups, m.FileName); var tombstone = path + ".delete-" + Guid.NewGuid().ToString("N"); File.Move(path, tombstone); try { await DeleteMetadataAsync(m.Id, ct); File.Delete(tombstone); } catch { if (File.Exists(tombstone) && !File.Exists(path)) File.Move(tombstone, path); throw; } } finally { gate.Release(); }
    }
    public Task RestoreAsync(string identity, CancellationToken ct = default) => RestoreAsync(identity, true, ct);
    public async Task RestoreAsync(string identity, bool confirm, CancellationToken ct = default)
    {
        if (!confirm) throw new InvalidOperationException("Restore confirmation is required.");
        await gate.WaitAsync(ct); try
        {
            EnsureStopped(); var m = await FindAsync(identity, ct) ?? throw new InvalidOperationException("The requested backup was not found."); var source = Contained(paths.Backups, m.FileName); await ValidateAsync(m, source, ct); var target = Contained(paths.Saves, SafeName(m.SourceSave, ".zip")); var rollback = target + ".rollback-" + Guid.NewGuid().ToString("N");
            try
            {
                if (File.Exists(target)) File.Copy(target, rollback, true); var temporary = target + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(source, temporary); File.Move(temporary, target, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                var settings = await store.GetAsync<ServerSettings>("settings", ct) ?? new ServerSettings(); await store.SetAsync("settings", settings with { ActiveSave = Path.GetFileName(target) }, ct);
            }
            catch { if (File.Exists(rollback)) File.Move(rollback, target, true); throw; }
            finally { if (File.Exists(rollback)) File.Delete(rollback); }
        } finally { gate.Release(); }
    }

    private void EnsureStopped() { if (supervisor?.IsRunning == true) throw new InvalidOperationException("Stop the server before changing backups."); }
    private static string SafeName(string? value, string extension) { if (string.IsNullOrWhiteSpace(value) || Path.GetFileName(value) != value || !value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The backup name is invalid."); return value; }
    private static string Contained(string root, string name) { var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar; var full = Path.GetFullPath(Path.Combine(root, name)); if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != name) throw new InvalidOperationException("The path is invalid."); return full; }
    private static async Task<string> HashAsync(string path, CancellationToken ct) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); }
    private static async Task ValidateAsync(BackupMetadata m, string path, CancellationToken ct) { if (!File.Exists(path) || new FileInfo(path).Length != m.Length || !string.Equals(await HashAsync(path, ct), m.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Backup integrity validation failed."); }
    private async Task<IReadOnlyList<BackupMetadata>> ReadAllAsync(CancellationToken ct) { await using var c = store.OpenConnection(); await c.OpenAsync(ct); var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,file_name,source_save,length,sha256,created_at_utc,updated_at_utc FROM backup_metadata ORDER BY created_at_utc DESC"; var result = new List<BackupMetadata>(); await using var r = await cmd.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) result.Add(Read(r)); return result; }
    private async Task<BackupMetadata?> FindAsync(string identity, CancellationToken ct) { if (string.IsNullOrWhiteSpace(identity) || Path.GetFileName(identity) != identity) return null; await using var c = store.OpenConnection(); await c.OpenAsync(ct); var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,file_name,source_save,length,sha256,created_at_utc,updated_at_utc FROM backup_metadata WHERE id=$id OR file_name=$id"; cmd.Parameters.AddWithValue("$id", identity); await using var r = await cmd.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? Read(r) : null; }
    private async Task InsertAsync(BackupMetadata m, CancellationToken ct) { await using var c=store.OpenConnection(); await c.OpenAsync(ct); var cmd=c.CreateCommand(); cmd.CommandText="INSERT INTO backup_metadata VALUES($id,$file,$source,$length,$hash,$created,$updated)"; Add(cmd,m); await cmd.ExecuteNonQueryAsync(ct); }
    private async Task UpdateAsync(BackupMetadata m, CancellationToken ct) { await using var c=store.OpenConnection(); await c.OpenAsync(ct); var cmd=c.CreateCommand(); cmd.CommandText="UPDATE backup_metadata SET file_name=$file,updated_at_utc=$updated WHERE id=$id"; cmd.Parameters.AddWithValue("$id",m.Id); cmd.Parameters.AddWithValue("$file",m.FileName); cmd.Parameters.AddWithValue("$updated",m.UpdatedAtUtc.ToString("O")); await cmd.ExecuteNonQueryAsync(ct); }
    private async Task DeleteMetadataAsync(string id, CancellationToken ct) { await using var c=store.OpenConnection(); await c.OpenAsync(ct); var cmd=c.CreateCommand(); cmd.CommandText="DELETE FROM backup_metadata WHERE id=$id"; cmd.Parameters.AddWithValue("$id",id); await cmd.ExecuteNonQueryAsync(ct); }
    private async Task PruneAsync(int retention, CancellationToken ct) { var all=await ReadAllAsync(ct); foreach(var m in all.Skip(Math.Max(1,retention))) { var path=Contained(paths.Backups,m.FileName); if(!File.Exists(path)) { await DeleteMetadataAsync(m.Id,ct); continue; } var tombstone=path+".prune-"+Guid.NewGuid().ToString("N"); File.Move(path,tombstone); try { await DeleteMetadataAsync(m.Id,ct); try { File.Delete(tombstone); } catch { /* metadata is gone; retain a recoverable tombstone */ } } catch { if(File.Exists(tombstone) && !File.Exists(path)) File.Move(tombstone,path); throw; } } }
    private static BackupMetadata Read(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),DateTimeOffset.Parse(r.GetString(5)),DateTimeOffset.Parse(r.GetString(6)));
    private static void Add(SqliteCommand c, BackupMetadata m) { c.Parameters.AddWithValue("$id",m.Id); c.Parameters.AddWithValue("$file",m.FileName); c.Parameters.AddWithValue("$source",m.SourceSave); c.Parameters.AddWithValue("$length",m.Length); c.Parameters.AddWithValue("$hash",m.Sha256); c.Parameters.AddWithValue("$created",m.CreatedAtUtc.ToString("O")); c.Parameters.AddWithValue("$updated",m.UpdatedAtUtc.ToString("O")); }
}
