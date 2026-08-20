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
builder.Services.AddSingleton<ServerSupervisor>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<VersionService>();
builder.Services.AddSingleton<ModService>();
builder.Services.AddSingleton<PlayerListService>();
builder.Services.AddSingleton<SystemHealthService>();
builder.Services.AddSingleton<MaintenanceStatusService>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "factorio_manager";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(5);
        limiter.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();
var store = app.Services.GetRequiredService<StateStore>();
await store.InitializeAsync(app.Lifetime.ApplicationStopping);
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
auth.MapPost("/setup", async (SetupRequest request, HttpContext context, SetupCodeService service, SecretStore secrets) =>
{
    if (!await service.TryConfigureAsync(request.Code, request.Password, context.RequestAborted))
        return Results.BadRequest(new { error = "Setup is unavailable, the code is invalid, or the password is too short." });
    await secrets.WriteAsync(new SecretSettings(request.FactorioUsername, request.FactorioToken), context.RequestAborted);
    return await SignInAsync(context);
}).RequireRateLimiting("login");
auth.MapPost("/login", async (LoginRequest request, HttpContext context, SetupCodeService service) =>
{
    if (!await service.VerifyPasswordAsync(request.Password, context.RequestAborted))
        return Results.Unauthorized();
    return await SignInAsync(context);
}).RequireRateLimiting("login");
auth.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization().AddEndpointFilter(CsrfFilter.Validate);
auth.MapGet("/me", (HttpContext context) => Results.Ok(new { csrfToken = context.User.FindFirstValue("csrf") })).RequireAuthorization();
auth.MapPost("/password", async (ChangePasswordRequest request, SetupCodeService service, HttpContext context) =>
{
    if (!await service.ChangePasswordAsync(request.CurrentPassword, request.NewPassword, context.RequestAborted))
        return Results.BadRequest(new { error = "Current password is incorrect or the new password is shorter than 12 characters." });
    return Results.NoContent();
}).RequireAuthorization().AddEndpointFilter(CsrfFilter.Validate);

var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(CsrfFilter.Validate);
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
api.MapGet("/backups", (DataPaths paths) => Results.Ok(Directory.EnumerateFiles(paths.Backups, "*.zip").Select(Path.GetFileName).OrderDescending()));
api.MapPost("/backups/restore/{backupName}", async (string backupName, BackupService backups, ServerSupervisor supervisor, HttpContext context) =>
{
    if (supervisor.IsRunning) return Results.Conflict(new { error = "Stop the server before restoring a backup." });
    try { await backups.RestoreAsync(Path.GetFileName(backupName), context.RequestAborted); return Results.NoContent(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
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
api.MapPost("/mods/install", async (ModInstallRequest request, ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.InstallAsync(request, context.RequestAborted)); }
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

api.MapGet("/players/{kind}", async (string kind, PlayerListService players, HttpContext context) => Results.Ok(await players.ListAsync(kind, context.RequestAborted)));
api.MapPost("/players/{kind}", async (string kind, PlayerListRequest request, PlayerListService players, HttpContext context) => Results.Ok(await players.AddAsync(kind, request.PlayerName, context.RequestAborted)));
api.MapDelete("/players/{kind}/{name}", async (string kind, string name, PlayerListService players, HttpContext context) => Results.Ok(await players.RemoveAsync(kind, name, context.RequestAborted)));

app.MapHub<StatusHub>("/hubs/status").RequireAuthorization();
app.MapFallbackToFile("index.html");
app.Run();

static async Task<IResult> SignInAsync(HttpContext context)
{
    var csrf = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin"), new Claim("csrf", csrf)], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(new ClaimsPrincipal(identity));
    return Results.Ok(new { csrfToken = csrf });
}

public partial class Program;
