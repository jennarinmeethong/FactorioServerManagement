using System.Security.Claims;
using System.Threading.RateLimiting;
using FactorioManager.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
var dataPaths = new DataPaths(builder.Configuration);
dataPaths.EnsureCreated();
builder.Services.AddSingleton(dataPaths);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPaths.Config, "keys")));
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<SetupCodeService>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<ServerSupervisor>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<VersionService>();
builder.Services.AddSingleton<ModService>();
builder.Services.AddSingleton<PlayerListService>();
builder.Services.AddSingleton<SourceRconClient>();
builder.Services.AddSingleton<LivePlayerService>();
builder.Services.AddSingleton<SystemHealthService>();
builder.Services.AddSingleton<MaintenanceStatusService>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "factorio_manager_v2";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var uid = context.Principal?.FindFirstValue("uid");
            var stamp = context.Principal?.FindFirstValue("security_stamp");
            var account = uid is null ? null : await context.HttpContext.RequestServices.GetRequiredService<AccountService>().FindAsync(uid, context.HttpContext.RequestAborted);
            if (account is null || !string.Equals(stamp, account.SecurityStamp, StringComparison.Ordinal)) context.RejectPrincipal();
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy("owner", p => p.RequireClaim("role", "owner")));
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(5);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("live-control", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();
var store = app.Services.GetRequiredService<StateStore>();
await store.InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<ModService>().InitializeAsync(app.Lifetime.ApplicationStopping);
var setup = app.Services.GetRequiredService<SetupCodeService>();
if (!await setup.IsConfiguredAsync(app.Lifetime.ApplicationStopping))
    app.Logger.LogWarning("Factorio Manager first-run setup code: {SetupCode}", setup.Code);

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", async (ServerSupervisor supervisor) => Results.Ok(await supervisor.GetStatusAsync()));

var auth = app.MapGroup("/api/auth");
auth.MapGet("/status", async (SetupCodeService service) => Results.Ok(new { configured = await service.IsConfiguredAsync() }));
auth.MapPost("/setup", async (SetupRequest request, HttpContext context, SetupCodeService service, SecretStore secrets, AccountService accounts, AuditService audit) =>
{
    if (!await service.TryConfigureAsync(request.Code, request.Password, context.RequestAborted))
        return Results.BadRequest(new { error = "Setup is unavailable, the code is invalid, or the password is too short." });
    await secrets.WriteAsync(new SecretSettings(request.FactorioUsername, request.FactorioToken), context.RequestAborted);
    await accounts.EnsureOwnerFromLegacyAsync(context.RequestAborted); await audit.WriteAsync("setup","user",null,"success",null,context.RequestAborted); return await SignInAsync(context, accounts, "admin");
}).RequireRateLimiting("login");
auth.MapPost("/login", async (LoginRequest request, HttpContext context, SetupCodeService service, AccountService accounts, AuditService audit) =>
{
    var verified = await accounts.VerifyAsync(string.IsNullOrWhiteSpace(request.Username) ? "admin" : request.Username, request.Password, context.RequestAborted);
    if (verified is null) { await audit.WriteAsync("login","user",null,"failure",null,context.RequestAborted); return Results.Unauthorized(); }
    await audit.WriteAsync("login","user",verified.Value.User.Id,"success",verified.Value.User.Id,context.RequestAborted); return await SignInAsync(context, accounts, verified.Value.User.Username);
}).RequireRateLimiting("login");
auth.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization().AddEndpointFilter(CsrfFilter.Validate);
auth.MapGet("/me", (HttpContext context) => Results.Ok(new { csrfToken = context.User.FindFirstValue("csrf"), uid = context.User.FindFirstValue("uid"), name = context.User.Identity?.Name, role = context.User.FindFirstValue("role") })).RequireAuthorization();
auth.MapPost("/password", async (ChangePasswordRequest request, SetupCodeService service, AccountService accounts, AuditService audit, HttpContext context) =>
{
    var uid = context.User.FindFirstValue("uid");
    var username = context.User.Identity?.Name;
    var verified = uid is not null && username is not null && (await accounts.VerifyAsync(username, request.CurrentPassword, context.RequestAborted)) is not null;
    if (!verified || request.NewPassword.Length < 12 || uid is null || !await accounts.ChangePasswordAsync(uid, request.NewPassword, context.RequestAborted))
        return Results.BadRequest(new { error = "Current password is incorrect or the new password is shorter than 12 characters." });
    await audit.WriteAsync("password_change","user",uid,"success",uid,context.RequestAborted); return Results.NoContent();
}).RequireAuthorization().AddEndpointFilter(CsrfFilter.Validate);

var owner = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter((EndpointFilterInvocationContext context, EndpointFilterDelegate next) => OwnerAuthorizeAsync(context, next));
owner.MapGet("/users", async (AccountService a, CancellationToken ct) => Results.Ok(await a.ListAsync(ct)));
owner.MapPost("/users", async (CreateUserRequest r, AccountService a, AuditService audit, HttpContext c) => { try { var u=await a.CreateAsync(r.Username,r.Password,r.Role,c.RequestAborted); await audit.WriteAsync("user_create","user",u.Id,"success",c.User.FindFirstValue("uid"),c.RequestAborted); return (IResult)Results.Ok(u); } catch { return (IResult)Results.BadRequest(); } }).AddEndpointFilter(CsrfFilter.Validate);
owner.MapPatch("/users/{id}/role", async (string id, ChangeRoleRequest r, AccountService a, AuditService audit, HttpContext c) => { var ok=await a.UpdateRoleAsync(id,r.Role,c.RequestAborted); await audit.WriteAsync("role_change","user",id,ok?"success":"failure",c.User.FindFirstValue("uid"),c.RequestAborted); return (IResult)(ok?Results.NoContent():Results.BadRequest()); }).AddEndpointFilter(CsrfFilter.Validate);
owner.MapPost("/users/{id}/password", async (string id, UserPasswordRequest r, AccountService a, AuditService audit, HttpContext c) => { var ok=await a.ChangePasswordAsync(id,r.Password,c.RequestAborted); await audit.WriteAsync("password_change","user",id,ok?"success":"failure",c.User.FindFirstValue("uid"),c.RequestAborted); return (IResult)(ok?Results.NoContent():Results.BadRequest()); }).AddEndpointFilter(CsrfFilter.Validate);
owner.MapDelete("/users/{id}", async (string id, AccountService a, AuditService audit, HttpContext c) => { if(id==c.User.FindFirstValue("uid")) return (IResult)Results.BadRequest(); var ok=await a.DeleteAsync(id,c.RequestAborted); await audit.WriteAsync("user_delete","user",id,ok?"success":"failure",c.User.FindFirstValue("uid"),c.RequestAborted); return (IResult)(ok?Results.NoContent():Results.BadRequest()); }).AddEndpointFilter(CsrfFilter.Validate);
owner.MapGet("/audit-events", async (long? beforeId, int? limit, AuditService a, CancellationToken ct) => Results.Ok(await a.ListAsync(beforeId,limit??50,ct)));

var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(CsrfFilter.Validate);
api.AddEndpointFilter((EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
    AuditAndAuthorizeMutationAsync(context, next));
api.MapGet("/status", async (ServerSupervisor supervisor) => Results.Ok(await supervisor.GetStatusAsync()));
api.MapGet("/settings", async (StateStore state, HttpContext context) => Results.Ok(await state.GetAsync<ServerSettings>("settings", context.RequestAborted)));
api.MapPut("/settings", async (ServerSettings settings, StateStore state, ServerSupervisor supervisor, HttpContext context) =>
{
    if (supervisor.IsRunning)
        return Results.Conflict(new { error = "Stop the server before changing settings." });
    var mapErrors = MapGenerationSettingsValidator.Validate(settings.MapGeneration);
    if (settings.MaxPlayers is < 0 or > 500 || settings.AutosaveMinutes is < 1 or > 120 || settings.BackupRetention is < 1 or > 365 || mapErrors.Count > 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = ["One or more setting values are outside their allowed range."] });
    await state.SetAsync("settings", settings, context.RequestAborted);
    return Results.Ok(settings);
});
api.MapPost("/control/{action}", async (string action, ServerSupervisor supervisor, HttpContext context) =>
{
    try
    {
        return action.ToLowerInvariant() switch
        {
            "start" => Results.Ok(await supervisor.StartAsync(context.RequestAborted)),
            "stop" => Results.Ok(await supervisor.StopAsync(context.RequestAborted)),
            "restart" => Results.Ok(await supervisor.RestartAsync(context.RequestAborted)),
            _ => Results.NotFound()
        };
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapGet("/logs", async (ServerSupervisor supervisor, HttpContext context) => Results.Ok(await supervisor.ReadLogsAsync(context.RequestAborted)));
api.MapGet("/logs/download", (DataPaths paths) =>
    File.Exists(Path.Combine(paths.Logs, "factorio.log"))
        ? Results.File(Path.Combine(paths.Logs, "factorio.log"), "text/plain", "factorio.log")
        : Results.NotFound(new { error = "No log file is available yet." }));
api.MapGet("/system-health", (SystemHealthService health) => Results.Ok(health.GetSnapshot()));
api.MapGet("/maintenance", async (MaintenanceStatusService maintenance, HttpContext context) => Results.Ok(await maintenance.GetAsync(context.RequestAborted)));

api.MapGet("/saves", (DataPaths paths) => Results.Ok(Directory.EnumerateFiles(paths.Saves, "*.zip").Select(Path.GetFileName).Order()));
api.MapPost("/saves/create", async (SaveCreateRequest request, ServerSupervisor supervisor, HttpContext context) =>
{
    try { return Results.Ok(new { name = await supervisor.CreateSaveAsync(request.Name, context.RequestAborted) }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapPost("/saves/select/{saveName}", async (string saveName, StateStore state, DataPaths paths, ServerSupervisor supervisor, HttpContext context) =>
{
    if (supervisor.IsRunning) return Results.Conflict(new { error = "Stop the server before switching saves." });
    var safe = Path.GetFileName(saveName);
    if (!string.Equals(safe, saveName, StringComparison.Ordinal) || !File.Exists(Path.Combine(paths.Saves, safe))) return Results.NotFound();
    var settings = await state.GetAsync<ServerSettings>("settings", context.RequestAborted) ?? new ServerSettings();
    settings = settings with { ActiveSave = safe };
    await state.SetAsync("settings", settings, context.RequestAborted);
    return Results.Ok(settings);
});
api.MapPost("/saves/upload", async (IFormFile file, DataPaths paths, ServerSupervisor supervisor, HttpContext context) =>
{
    if (supervisor.IsRunning) return Results.Conflict(new { error = "Stop the server before uploading a save." });
    var safe = Path.GetFileName(file.FileName);
    if (!safe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.Length == 0 || file.Length > 1024L * 1024 * 1024)
        return Results.BadRequest(new { error = "Upload a Factorio .zip save no larger than 1 GB." });
    await using var output = File.Create(Path.Combine(paths.Saves, safe));
    await file.CopyToAsync(output, context.RequestAborted);
    return Results.Created($"/api/saves/{Uri.EscapeDataString(safe)}", new { name = safe });
});
api.MapPost("/saves/backup", async (BackupService backups, HttpContext context) => Results.Ok(new { name = await backups.CreateBackupAsync("manual", context.RequestAborted) }));
api.MapGet("/backups", async (BackupService backups, CancellationToken ct) => Results.Ok(await backups.ListAsync(ct)));
api.MapGet("/backups/{backupId}/download", async (string backupId, BackupService backups, HttpContext context) =>
{
    try { var result = await backups.OpenReadAsync(backupId, context.RequestAborted); return Results.File(result!.Value.Content, "application/zip", result.Value.Metadata.FileName); }
    catch (InvalidOperationException exception) { return Results.NotFound(new { error = exception.Message }); }
});
api.MapPost("/backups/{backupId}/restore", async (string backupId, BackupRestoreRequest request, BackupService backups, ServerSupervisor supervisor, HttpContext context) =>
{
    if (!context.User.HasClaim("role", "owner")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (supervisor.IsRunning) return Results.Conflict(new { error = "Stop the server before restoring a backup." });
    if (!request.Confirm) return Results.BadRequest(new { error = "Explicit restore confirmation is required." });
    try { await backups.RestoreAsync(backupId, true, context.RequestAborted); return Results.NoContent(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapPatch("/backups/{backupId}", async (string backupId, BackupRenameRequest request, BackupService backups, HttpContext context) =>
{
    if (!context.User.HasClaim("role", "owner")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { await backups.RenameAsync(backupId, request.Name, context.RequestAborted); return Results.NoContent(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapDelete("/backups/{backupId}", async (string backupId, BackupService backups, HttpContext context) =>
{
    if (!context.User.HasClaim("role", "owner")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { await backups.DeleteAsync(backupId, context.RequestAborted); return Results.NoContent(); }
    catch (InvalidOperationException exception) { return Results.NotFound(new { error = exception.Message }); }
});

api.MapGet("/versions", (VersionService versions) => Results.Ok(versions.GetCachedVersions()));
api.MapGet("/versions/catalog", async (VersionService versions, HttpContext context) => Results.Ok(await versions.GetCatalogAsync(context.RequestAborted)));
api.MapGet("/updates", async (VersionService versions, HttpContext context) => Results.Ok(await versions.GetUpdateStatusAsync(context.RequestAborted)));
api.MapPost("/updates/check", async (VersionService versions, HttpContext context) => Results.Ok(await versions.CheckForUpdateAsync(context.RequestAborted)));
api.MapPost("/versions/download/{channel}/{version}", async (string channel, string version, VersionService versions, HttpContext context) =>
{
    try { return Results.Ok(await versions.DownloadAsync(channel, version, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapPost("/versions/apply", async (VersionApplyRequest request, VersionService versions, HttpContext context) =>
{
    try { return Results.Ok(await versions.ApplyAsync(request, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

api.MapGet("/mods", async (ModService mods, HttpContext context) => Results.Ok(await mods.ListAsync(context.RequestAborted)));
api.MapGet("/mods/search", async (string query, ModService mods, HttpContext context) => Results.Ok(await mods.SearchAsync(query, context.RequestAborted)));
api.MapGet("/mods/recovery", async (ModService mods, HttpContext context) => Results.Ok(await mods.GetRecoveryStatusAsync(context.RequestAborted)));
api.MapPost("/mods/preflight", async (ModPreflightRequest request, ModService mods, HttpContext context) => Results.Ok(await mods.PreflightAsync(request, context.RequestAborted)));
api.MapPost("/mods/install", async (ModInstallRequest request, ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.InstallAsync(request, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapPost("/mods/upload", async (HttpRequest request, ModService mods, HttpContext context) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Upload the mod as multipart/form-data." });
    var form = await request.ReadFormAsync(context.RequestAborted);
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest(new { error = "Choose a mod .zip file." });
    if (file.Length > 512L * 1024 * 1024) return Results.BadRequest(new { error = "The mod archive exceeds the 512 MB limit." });
    var enabled = !bool.TryParse(form["enabled"], out var parsedEnabled) || parsedEnabled;
    var includeDependencies = !bool.TryParse(form["includeDependencies"], out var parsedDependencies) || parsedDependencies;
    try
    {
        await using var stream = file.OpenReadStream();
        return Results.Ok(await mods.UploadAsync(stream, file.FileName, new ModUploadRequest(enabled, includeDependencies, true), context.RequestAborted));
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapGet("/mods/updates", async (ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.CheckUpdatesAsync(context.RequestAborted)); }
    catch (HttpRequestException exception) { return Results.BadRequest(new { error = $"Mod Portal update check failed: {exception.Message}" }); }
});
api.MapPost("/mods/{name}/update", async (string name, ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.UpdateAsync(name, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (HttpRequestException exception) { return Results.BadRequest(new { error = $"Mod Portal update failed: {exception.Message}" }); }
});
api.MapPost("/mods/{name}/enabled/{enabled:bool}", async (string name, bool enabled, ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.SetEnabledAsync(name, enabled, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapDelete("/mods/{name}", async (string name, bool? force, ModService mods, HttpContext context) =>
{
    if (force == true && !context.User.HasClaim("role", "owner")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(await mods.UninstallAsync(name, force == true, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
});
api.MapPost("/mods/bulk-update", async (ModBulkUpdateRequest request, ModService mods, HttpContext context) =>
{
    if (!request.Confirm) return Results.BadRequest(new { error = "Explicit bulk update confirmation is required." });
    try { return Results.Ok(await mods.BulkUpdateAsync(request.Names ?? [], context.RequestAborted)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
api.MapGet("/mods/profiles", async (ModService mods, HttpContext context) => Results.Ok(await mods.ProfilesAsync(context.RequestAborted)));
api.MapPost("/mods/profiles", async (ModProfileRequest request, ModService mods, HttpContext context) => { try { return Results.Ok(await mods.SaveProfileAsync(request, context.RequestAborted)); } catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message }); } });
api.MapPost("/mods/profiles/{name}/apply", async (string name, ModService mods, HttpContext context) => { try { return Results.Ok(await mods.ApplyProfileAsync(name, context.RequestAborted)); } catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message }); } });
api.MapDelete("/mods/profiles/{name}", async (string name, ModService mods, HttpContext context) => Results.Ok(new { deleted = await mods.DeleteProfileAsync(name, context.RequestAborted) }));

api.MapGet("/players/{kind}", async (string kind, PlayerListService players, HttpContext context) => Results.Ok(await players.ListAsync(kind, context.RequestAborted)));
api.MapPost("/players/{kind}", async (string kind, PlayerListRequest request, PlayerListService players, HttpContext context) => Results.Ok(await players.AddAsync(kind, request.PlayerName, context.RequestAborted)));
api.MapDelete("/players/{kind}/{name}", async (string kind, string name, PlayerListService players, HttpContext context) => Results.Ok(await players.RemoveAsync(kind, name, context.RequestAborted)));

api.MapGet("/live-players", async (LivePlayerService players, HttpContext context) =>
{
    try { return Results.Ok(await players.ListAsync(context.RequestAborted)); }
    catch (ServerNotRunningException e) { return Results.Conflict(new { error = e.Message }); }
    catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
});
api.MapPost("/live-chat", async (LiveChatRequest request, LivePlayerService players, HttpContext context) =>
{
    try { await players.ChatAsync(request.Message, context.RequestAborted); return Results.NoContent(); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (ServerNotRunningException e) { return Results.Conflict(new { error = e.Message }); }
    catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
}).RequireRateLimiting("live-control");
api.MapPost("/live-players/kick", async (LivePlayerActionRequest request, LivePlayerService players, HttpContext context) =>
{
    try { await players.KickAsync(request.PlayerName, context.RequestAborted); return Results.NoContent(); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (ServerNotRunningException e) { return Results.Conflict(new { error = e.Message }); }
    catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
}).RequireRateLimiting("live-control");
api.MapPost("/live-players/ban", async (LivePlayerActionRequest request, LivePlayerService players, HttpContext context) =>
{
    try { await players.BanAsync(request.PlayerName, request.Reason, context.RequestAborted); return Results.NoContent(); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (ServerNotRunningException e) { return Results.Conflict(new { error = e.Message }); }
    catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
}).RequireRateLimiting("live-control");

app.MapHub<StatusHub>("/hubs/status").RequireAuthorization();
app.MapFallbackToFile("index.html");
app.Run();

static async Task<IResult> SignInAsync(HttpContext context, AccountService accounts, string username)
{
    var csrf = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    var user = await accounts.FindByUsernameAsync(username, context.RequestAborted) ?? throw new InvalidOperationException("Account not found");
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.Username), new Claim("uid", user.Id), new Claim("role", user.Role.ToString().ToLowerInvariant()), new Claim("security_stamp", user.SecurityStamp), new Claim("csrf", csrf)], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(new ClaimsPrincipal(identity));
    return Results.Ok(new { csrfToken = csrf });
}

static async ValueTask<object?> AuditAndAuthorizeMutationAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var request = context.HttpContext.Request;
    if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) return await next(context);
    var audit = context.HttpContext.RequestServices.GetRequiredService<AuditService>();
    var actor = context.HttpContext.User.FindFirstValue("uid");
    var action = $"{request.Method} {request.Path.Value}";
    if (!context.HttpContext.User.HasClaim("role", "admin") && !context.HttpContext.User.HasClaim("role", "owner"))
    {
        await audit.WriteAsync(action, "endpoint", null, "denied", actor, context.HttpContext.RequestAborted);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    try
    {
        var result = await next(context);
        var status = (result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
        var outcome = status is 401 or 403 ? "denied" : status is >= 400 ? "failure" : "success";
        await audit.WriteAsync(action, "endpoint", null, outcome, actor, context.HttpContext.RequestAborted);
        return result;
    }
    catch
    {
        await audit.WriteAsync(action, "endpoint", null, "failure", actor, context.HttpContext.RequestAborted);
        throw;
    }
}

static async ValueTask<object?> OwnerAuthorizeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    if (context.HttpContext.User.HasClaim("role", "owner")) return await next(context);
    var audit = context.HttpContext.RequestServices.GetRequiredService<AuditService>();
    await audit.WriteAsync($"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}", "endpoint", null, "denied", context.HttpContext.User.FindFirstValue("uid"), context.HttpContext.RequestAborted);
    return Results.StatusCode(StatusCodes.Status403Forbidden);
}

public partial class Program;
