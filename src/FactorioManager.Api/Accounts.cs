using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FactorioManager.Api;

public sealed class AccountService(StateStore state)
{
    public async Task EnsureInitialOwnerAsync(CancellationToken ct = default)
    {
        await using var connection = state.OpenConnection();
        await connection.OpenAsync(ct);
        var ownerCheck = connection.CreateCommand();
        ownerCheck.CommandText = "SELECT 1 FROM users WHERE role='owner' LIMIT 1";
        var hasOwner = await ownerCheck.ExecuteScalarAsync(ct) is not null;

        var adminLookup = connection.CreateCommand();
        adminLookup.CommandText = "SELECT id, role FROM users WHERE username='admin' LIMIT 1";
        string? adminId = null;
        string? adminRole = null;
        await using (var adminReader = await adminLookup.ExecuteReaderAsync(ct))
        {
            if (await adminReader.ReadAsync(ct))
            {
                adminId = adminReader.GetString(0);
                adminRole = adminReader.GetString(1);
            }
        }

        if (adminId is null)
        {
            var hash = await state.GetAsync<string>("admin_password", ct);
            if (hash is null)
                return;

            var insertNow = DateTimeOffset.UtcNow.ToString("O");
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO users(id,username,password_hash,role,security_stamp,created_at_utc,updated_at_utc) VALUES($id,'admin',$hash,'owner',$stamp,$now,$now)";
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$now", insertNow);
            await insert.ExecuteNonQueryAsync(ct);
            return;
        }

        // The canonical bootstrap account must never remain a viewer. Do not gate
        // this migration on app_state.admin_password: a failed/interrupted setup
        // can persist one half of the bootstrap state, and startup must repair it.
        var targetRole = !hasOwner ? "owner" : adminRole!.Equals("viewer", StringComparison.OrdinalIgnoreCase) ? "admin" : null;
        if (targetRole is null)
            return;

        var now = DateTimeOffset.UtcNow.ToString("O");
        var promote = connection.CreateCommand();
        promote.CommandText = "UPDATE users SET role=$role, security_stamp=$stamp, updated_at_utc=$now WHERE id=$id";
        promote.Parameters.AddWithValue("$role", targetRole);
        promote.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N"));
        promote.Parameters.AddWithValue("$now", now);
        promote.Parameters.AddWithValue("$id", adminId);
        await promote.ExecuteNonQueryAsync(ct);
    }

    public async Task EnsureOwnerFromLegacyAsync(CancellationToken ct=default) { var existing=await FindByUsernameAsync("admin",ct); if(existing is not null)return; var hash=await state.GetAsync<string>("admin_password",ct); if(hash is null)return; await using var c=state.OpenConnection(); await c.OpenAsync(ct); var now=DateTimeOffset.UtcNow.ToString("O"); var q=c.CreateCommand(); q.CommandText="INSERT INTO users(id,username,password_hash,role,security_stamp,created_at_utc,updated_at_utc) VALUES($id,'admin',$hash,'owner',$stamp,$now,$now)";q.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$hash",hash);q.Parameters.AddWithValue("$stamp",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$now",now);await q.ExecuteNonQueryAsync(ct); }
    public async Task<UserRecord?> FindAsync(string id, CancellationToken ct = default) { await using var c = state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText="SELECT id,username,role,security_stamp,created_at_utc,updated_at_utc FROM users WHERE id=$id"; q.Parameters.AddWithValue("$id",id); await using var r=await q.ExecuteReaderAsync(ct); return await r.ReadAsync(ct)? Read(r):null; }
    public async Task<UserRecord?> FindByUsernameAsync(string username, CancellationToken ct = default) { await using var c=state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText="SELECT id,username,role,security_stamp,created_at_utc,updated_at_utc FROM users WHERE username=$name"; q.Parameters.AddWithValue("$name",username); await using var r=await q.ExecuteReaderAsync(ct); return await r.ReadAsync(ct)? Read(r):null; }
    public async Task<(UserRecord User,string Hash)?> VerifyAsync(string username,string password,CancellationToken ct=default) { if (password.Length < 8) return null; await using var c=state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText="SELECT id,username,role,security_stamp,created_at_utc,updated_at_utc,password_hash FROM users WHERE username=$name"; q.Parameters.AddWithValue("$name",username); await using var r=await q.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return null; var hash=r.GetString(6); return BCrypt.Net.BCrypt.Verify(password,hash)?(Read(r),hash):null; }
    public async Task<List<UserRecord>> ListAsync(CancellationToken ct=default) { await using var c=state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText="SELECT id,username,role,security_stamp,created_at_utc,updated_at_utc FROM users ORDER BY username"; await using var r=await q.ExecuteReaderAsync(ct); var list=new List<UserRecord>(); while(await r.ReadAsync(ct)) list.Add(Read(r)); return list; }
    public async Task<UserRecord> CreateAsync(string username,string password,UserRole role,CancellationToken ct=default) { if(username.Length<1||password.Length<8||role==UserRole.Owner) throw new InvalidOperationException("Invalid account."); var id=Guid.NewGuid().ToString("N"); var stamp=Guid.NewGuid().ToString("N"); var now=DateTimeOffset.UtcNow; await using var c=state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText="INSERT INTO users(id,username,password_hash,role,security_stamp,created_at_utc,updated_at_utc) VALUES($id,$name,$hash,$role,$stamp,$now,$now)"; q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$name",username);q.Parameters.AddWithValue("$hash",BCrypt.Net.BCrypt.HashPassword(password));q.Parameters.AddWithValue("$role",Role(role));q.Parameters.AddWithValue("$stamp",stamp);q.Parameters.AddWithValue("$now",now.ToString("O")); await q.ExecuteNonQueryAsync(ct); return new(id,username,role,stamp,now,now); }
    public async Task<bool> UpdateRoleAsync(string id,UserRole role,CancellationToken ct=default) { if(role==UserRole.Owner)return false; return await UpdateAsync(id,"role",Role(role),true,ct); }
    public async Task<bool> ChangePasswordAsync(string id,string password,CancellationToken ct=default) { if(password.Length<8)return false; return await UpdateAsync(id,"password_hash",BCrypt.Net.BCrypt.HashPassword(password),true,ct); }
    public async Task<bool> DeleteAsync(string id,CancellationToken ct=default) { await using var c=state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText="DELETE FROM users WHERE id=$id AND role<>'owner'"; q.Parameters.AddWithValue("$id",id); return await q.ExecuteNonQueryAsync(ct)>0; }
    private async Task<bool> UpdateAsync(string id,string field,string value,bool stamp,CancellationToken ct){ await using var c=state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText=$"UPDATE users SET {field}=$value, security_stamp=CASE WHEN $stamp=1 THEN $new ELSE security_stamp END, updated_at_utc=$now WHERE id=$id AND role<>'owner'"; q.Parameters.AddWithValue("$value",value);q.Parameters.AddWithValue("$stamp",stamp?1:0);q.Parameters.AddWithValue("$new",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));q.Parameters.AddWithValue("$id",id); return await q.ExecuteNonQueryAsync(ct)>0; }
    private static string Role(UserRole r)=>r.ToString().ToLowerInvariant();
    private static UserRecord Read(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),Enum.Parse<UserRole>(r.GetString(2),true),r.GetString(3),DateTimeOffset.Parse(r.GetString(4)),DateTimeOffset.Parse(r.GetString(5)));
}

public sealed class AuditService(StateStore state)
{
    private readonly ILogger<AuditService>? logger;
    public AuditService(StateStore state, ILogger<AuditService> logger) : this(state) => this.logger = logger;
    public async Task WriteAsync(string action,string targetType,string? targetId,string outcome,string? actor,CancellationToken ct=default) { try { await using var c=state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText="INSERT INTO audit_events(occurred_at_utc,actor_user_id,action,target_type,target_id,outcome,details_json) VALUES($at,$actor,$action,$type,$target,$outcome,'{}')"; q.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); q.Parameters.AddWithValue("$actor",(object?)actor??DBNull.Value);q.Parameters.AddWithValue("$action",action);q.Parameters.AddWithValue("$type",targetType);q.Parameters.AddWithValue("$target",(object?)targetId??DBNull.Value);q.Parameters.AddWithValue("$outcome",outcome); await q.ExecuteNonQueryAsync(ct); } catch(Exception ex) { logger?.LogWarning(ex,"Audit event write failed for action {Action}", action); } }
    public async Task<List<AuditEvent>> ListAsync(long? before,int limit,CancellationToken ct=default) { limit=Math.Clamp(limit,1,100); await using var c=state.OpenConnection(); await c.OpenAsync(ct); var q=c.CreateCommand(); q.CommandText="SELECT id,occurred_at_utc,actor_user_id,action,target_type,target_id,outcome,details_json FROM audit_events WHERE ($before IS NULL OR id<$before) ORDER BY id DESC LIMIT $limit"; q.Parameters.AddWithValue("$before",(object?)before??DBNull.Value);q.Parameters.AddWithValue("$limit",limit); await using var r=await q.ExecuteReaderAsync(ct); var list=new List<AuditEvent>(); while(await r.ReadAsync(ct)) list.Add(new(r.GetInt64(0),DateTimeOffset.Parse(r.GetString(1)),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.GetString(6),r.GetString(7))); return list; }
}
