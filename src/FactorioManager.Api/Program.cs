using System.Security.Claims;
using System.IO.Compression;
using System.Threading.RateLimiting;
using FactorioManager.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => ApiJsonOptions.Configure(options.SerializerOptions));
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
builder.Services.AddSingleton<MaintenanceHistoryService>();
builder.Services.AddSingleton<ServerEventHistoryService>();
builder.Services.AddSingleton<ModService>();
builder.Services.AddSingleton<PlayerListService>();
builder.Services.AddSingleton<SourceRconClient>();
builder.Services.AddSingleton<LivePlayerService>();
builder.Services.AddSingleton<SystemHealthService>();
builder.Services.AddSingleton<HealthHistoryService>();
builder.Services.AddSingleton<MaintenanceStatusService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // Bump the cookie name after the RBAC bootstrap migration so a browser
        // cannot keep presenting a role claim issued by an older build.
        options.Cookie.Name = "factorio_manager_v3";
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
            var role = context.Principal?.FindFirstValue("role");
            var account = uid is null ? null : await context.HttpContext.RequestServices.GetRequiredService<AccountService>().FindAsync(uid, context.HttpContext.RequestAborted);
            if (account is null ||
                !string.Equals(stamp, account.SecurityStamp, StringComparison.Ordinal) ||
                !string.Equals(role, account.Role.ToString(), StringComparison.OrdinalIgnoreCase))
                context.RejectPrincipal();
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
await app.Services.GetRequiredService<AccountService>().EnsureInitialOwnerAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<ModService>().InitializeAsync(app.Lifetime.ApplicationStopping);
var setup = app.Services.GetRequiredService<SetupCodeService>();
if (!await setup.IsConfiguredAsync(app.Lifetime.ApplicationStopping))
    app.Logger.LogWarning("Factorio Manager first-run setup is pending; use the configured setup-code channel to complete setup.");

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
    try
    {
        await next(context);
    }
    catch (Exception exception)
    {
        app.Logger.LogError("Unhandled request failure {RequestId}: {Error}", context.TraceIdentifier, SafeDiagnostics.Redact(exception.ToString()));
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await ApiErrors.Create(context, StatusCodes.Status500InternalServerError, "The server could not complete this request. Try again and provide the request id to an administrator.", "Unexpected server error").ExecuteAsync(context);
        }
    }
});
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    if (response.StatusCode >= 400 && !response.HasStarted && string.IsNullOrWhiteSpace(response.ContentType))
        await ApiErrors.Create(statusContext.HttpContext, response.StatusCode, response.StatusCode switch
        {
            401 => "Sign in is required.",
            403 => "You do not have permission to perform this action.",
            404 => "The requested resource was not found.",
            429 => "Too many requests. Wait a moment and try again.",
            _ => "The request could not be completed."
        }).ExecuteAsync(statusContext.HttpContext);
});

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
    var factorioUsername = request.FactorioUsername?.Trim();
    var factorioToken = request.FactorioToken?.Trim();
    var hasFactorioUsername = !string.IsNullOrWhiteSpace(factorioUsername);
    var hasFactorioToken = !string.IsNullOrWhiteSpace(factorioToken);
    if (hasFactorioUsername != hasFactorioToken)
        return ApiErrors.BadRequest(context, "Enter both the Factorio username and token, or leave both empty.");
    if (factorioUsername?.Length > 256 || factorioToken?.Length > 512)
        return ApiErrors.BadRequest(context, "The Factorio username or token is too long.");
    if (!await service.TryConfigureAsync(request.Code, request.Password, context.RequestAborted))
        return ApiErrors.BadRequest(context, "Setup is unavailable, the code is invalid, or the password is too short.", "Setup failed");
    await secrets.WriteAsync(new SecretSettings(factorioUsername, factorioToken), context.RequestAborted);
    await accounts.EnsureInitialOwnerAsync(context.RequestAborted); await audit.WriteAsync("setup","user",null,"success",null,context.RequestAborted); return await SignInAsync(context, accounts, "admin");
}).RequireRateLimiting("login");
auth.MapPost("/login", async (LoginRequest request, HttpContext context, SetupCodeService service, AccountService accounts, AuditService audit) =>
{
    var verified = await accounts.VerifyAsync(string.IsNullOrWhiteSpace(request.Username) ? "admin" : request.Username, request.Password, context.RequestAborted);
    if (verified is null) { await audit.WriteAsync("login","user",null,"failure",null,context.RequestAborted); return ApiErrors.Unauthorized(context, "The username or password is incorrect."); }
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
    if (!verified || request.NewPassword.Length < 8 || uid is null || !await accounts.ChangePasswordAsync(uid, request.NewPassword, context.RequestAborted))
        return ApiErrors.BadRequest(context, "Current password is incorrect or the new password is shorter than 8 characters.");
    await audit.WriteAsync("password_change","user",uid,"success",uid,context.RequestAborted); return Results.NoContent();
}).RequireAuthorization().AddEndpointFilter(CsrfFilter.Validate);

var owner = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter((EndpointFilterInvocationContext context, EndpointFilterDelegate next) => OwnerAuthorizeAsync(context, next));
owner.MapGet("/users", async (AccountService a, CancellationToken ct) => Results.Ok(await a.ListAsync(ct)));
owner.MapPost("/users", async (CreateUserRequest r, AccountService a, AuditService audit, HttpContext c) => { try { var u=await a.CreateAsync(r.Username,r.Password,r.Role,c.RequestAborted); await audit.WriteAsync("user_create","user",u.Id,"success",c.User.FindFirstValue("uid"),c.RequestAborted); return (IResult)Results.Ok(u); } catch (InvalidOperationException e) { return ApiErrors.BadRequest(c, e.Message, "User creation failed"); } }).AddEndpointFilter(CsrfFilter.Validate);
owner.MapPatch("/users/{id}/role", async (string id, ChangeRoleRequest r, AccountService a, AuditService audit, HttpContext c) => { var ok=await a.UpdateRoleAsync(id,r.Role,c.RequestAborted); await audit.WriteAsync("role_change","user",id,ok?"success":"failure",c.User.FindFirstValue("uid"),c.RequestAborted); return (IResult)(ok?Results.NoContent():ApiErrors.NotFound(c, "The user was not found.")); }).AddEndpointFilter(CsrfFilter.Validate);
owner.MapPost("/users/{id}/password", async (string id, UserPasswordRequest r, AccountService a, AuditService audit, HttpContext c) => { var ok=await a.ChangePasswordAsync(id,r.Password,c.RequestAborted); await audit.WriteAsync("password_change","user",id,ok?"success":"failure",c.User.FindFirstValue("uid"),c.RequestAborted); return (IResult)(ok?Results.NoContent():ApiErrors.NotFound(c, "The user was not found.")); }).AddEndpointFilter(CsrfFilter.Validate);
owner.MapDelete("/users/{id}", async (string id, AccountService a, AuditService audit, HttpContext c) => { if(id==c.User.FindFirstValue("uid")) return (IResult)ApiErrors.BadRequest(c, "You cannot delete your own account."); var ok=await a.DeleteAsync(id,c.RequestAborted); await audit.WriteAsync("user_delete","user",id,ok?"success":"failure",c.User.FindFirstValue("uid"),c.RequestAborted); return (IResult)(ok?Results.NoContent():ApiErrors.NotFound(c, "The user was not found.")); }).AddEndpointFilter(CsrfFilter.Validate);
owner.MapGet("/audit-events", async (long? beforeId, int? limit, AuditService a, CancellationToken ct) => Results.Ok(await a.ListAsync(beforeId,limit??50,ct)));
owner.MapGet("/notification-settings", async (NotificationService notifications, CancellationToken ct) => Results.Ok(await notifications.GetStatusAsync(ct)));
owner.MapPut("/notification-settings", async (NotificationSettingsRequest request, StateStore state, SecretStore secrets, HttpContext context) =>
{
    if (request.DiscordWebhookUrl?.Length > 2048 || request.TelegramBotToken?.Length > 512 || request.TelegramChatId?.Length > 256)
        return ApiErrors.BadRequest(context, "Notification settings are too long.");
    if (!string.IsNullOrWhiteSpace(request.DiscordWebhookUrl) &&
        (!Uri.TryCreate(request.DiscordWebhookUrl, UriKind.Absolute, out var webhook) ||
         (webhook.Scheme is not ("http" or "https"))))
        return ApiErrors.BadRequest(context, "Discord webhook URL must be an absolute HTTP or HTTPS URL.");
    // Empty fields are intentionally treated as "keep the current secret".
    // This lets the UI avoid echoing credentials while still allowing an
    // explicit clear through a separate, future operation.
    await secrets.UpdateNotificationsAsync(request.DiscordWebhookUrl, request.TelegramBotToken, request.TelegramChatId, context.RequestAborted);
    var saved = await secrets.ReadAsync(context.RequestAborted);
    var settings = await state.GetAsync<ServerSettings>("settings", context.RequestAborted) ?? new ServerSettings();
    await state.SetAsync("settings", settings with { AlertsEnabled = request.Enabled }, context.RequestAborted);
    return Results.Ok(new NotificationSettingsStatus(
        request.Enabled,
        !string.IsNullOrWhiteSpace(saved.DiscordWebhookUrl),
        !string.IsNullOrWhiteSpace(saved.TelegramBotToken) && !string.IsNullOrWhiteSpace(saved.TelegramChatId)));
}).AddEndpointFilter(CsrfFilter.Validate);
owner.MapGet("/config/export", async (StateStore state, CancellationToken ct) =>
{
    var settings = await state.GetAsync<ServerSettings>("settings", ct) ?? new ServerSettings();
    return Results.Ok(new ConfigurationBundle(
        1,
        DateTimeOffset.UtcNow,
        settings with { ServerPassword = null },
        await state.GetAsync<ModEntry[]>("mods", ct) ?? [],
        await state.GetAsync<ModProfile[]>("mod_profiles", ct) ?? []));
});
owner.MapPost("/config/import", async (ConfigurationImportRequest request, StateStore state, ServerSupervisor supervisor, HttpContext context) =>
{
    var bundle = request.Bundle;
    if (request.Confirm && !supervisor.IsRunning && bundle.SchemaVersion == 1 && bundle.Settings is not null && bundle.Mods is not null && bundle.ModProfiles is not null)
    {
        var errors = ServerSettingsValidator.Validate(bundle.Settings);
        if (errors.Count > 0) return ApiErrors.Validation(context, errors);
        var current = await state.GetAsync<ServerSettings>("settings", context.RequestAborted) ?? new ServerSettings();
        var importedSettings = bundle.Settings with { ServerPassword = bundle.Settings.ServerPassword ?? current.ServerPassword };
        await state.SetManyAsync(new Dictionary<string, object?>
        {
            ["settings"] = importedSettings,
            ["mods"] = bundle.Mods,
            ["mod_profiles"] = bundle.ModProfiles
        }, context.RequestAborted);
        return Results.NoContent();
    }
    return ApiErrors.BadRequest(context, "Stop the server and provide a supported configuration bundle.");
}).AddEndpointFilter(CsrfFilter.Validate);

var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(CsrfFilter.Validate);
api.AddEndpointFilter((EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
    AuditAndAuthorizeMutationAsync(context, next));
api.MapGet("/status", async (ServerSupervisor supervisor) => Results.Ok(await supervisor.GetStatusAsync()));
api.MapGet("/settings", async (StateStore state, HttpContext context) => Results.Ok(await state.GetAsync<ServerSettings>("settings", context.RequestAborted)));
api.MapGet("/factorio-credentials/status", async (SecretStore secrets, HttpContext context) =>
{
    var configured = await secrets.ReadAsync(context.RequestAborted);
    return Results.Ok(new
    {
        configured = !string.IsNullOrWhiteSpace(configured.FactorioUsername) && !string.IsNullOrWhiteSpace(configured.FactorioToken),
        username = configured.FactorioUsername
    });
});
api.MapPut("/factorio-credentials", async (FactorioCredentialsRequest request, SecretStore secrets, HttpContext context) =>
{
    var current = await secrets.ReadAsync(context.RequestAborted);
    var username = string.IsNullOrWhiteSpace(request.Username) ? current.FactorioUsername : request.Username.Trim();
    var token = string.IsNullOrWhiteSpace(request.Token) ? current.FactorioToken : request.Token.Trim();
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token) || username.Length > 256 || token.Length > 512)
        return ApiErrors.BadRequest(context, "Enter both the Factorio username and token.");
    await secrets.WriteAsync(new SecretSettings(username, token), context.RequestAborted);
    return Results.NoContent();
});
api.MapPut("/settings", async (ServerSettings settings, StateStore state, ServerSupervisor supervisor, HttpContext context) =>
{
    if (supervisor.IsRunning)
        return ApiErrors.Conflict(context, "Stop the server before changing settings.");
    var submittedProfiles = settings.MapGenerationProfiles ?? new Dictionary<string, MapGenerationSettings>(StringComparer.OrdinalIgnoreCase);
    var profiles = submittedProfiles.Count > 0
        ? new Dictionary<string, MapGenerationSettings>(submittedProfiles, StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, MapGenerationSettings>(StringComparer.OrdinalIgnoreCase);
    if (!profiles.ContainsKey("vanilla")) profiles["vanilla"] = settings.MapGeneration;
    if (!profiles.ContainsKey("space-age")) profiles["space-age"] = settings.MapGeneration;
    var normalized = settings with { MapGenerationProfiles = profiles };
    var validationErrors = ServerSettingsValidator.Validate(normalized);
    if (validationErrors.Count > 0)
        return ApiErrors.Validation(context, validationErrors);
    var activeMap = profiles[normalized.Expansion];
    normalized = normalized with { MapGeneration = activeMap };
    await state.SetAsync("settings", normalized, context.RequestAborted);
    return Results.Ok(normalized);
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
            _ => ApiErrors.NotFound(context, $"Unknown control action '{action}'.")
        };
    }
    catch (InvalidOperationException exception) { return ApiErrors.Create(context, StatusCodes.Status400BadRequest, exception.Message); }
});
api.MapGet("/logs", async (string? search, ServerSupervisor supervisor, HttpContext context) =>
{
    var logs = await supervisor.ReadLogsAsync(context.RequestAborted);
    if (!string.IsNullOrWhiteSpace(search)) logs = logs.Where(line => line.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    return Results.Ok(logs);
});
api.MapGet("/logs/download", async (DataPaths paths, HttpContext context) =>
{
    var logPath = Path.Combine(paths.Logs, "factorio.log");
    if (!File.Exists(logPath)) return ApiErrors.Create(context, StatusCodes.Status404NotFound, "No log file is available yet.");
    context.Response.Headers.ContentDisposition = "attachment; filename=\"factorio.log\"";
    var content = SafeDiagnostics.Redact(await File.ReadAllTextAsync(logPath, context.RequestAborted));
    return Results.Text(content, "text/plain");
});
api.MapGet("/system-health", async (SystemHealthService health, HealthHistoryService history, CancellationToken ct) =>
{
    var snapshot = health.GetSnapshot();
    await history.RecordAsync(snapshot, ct);
    return Results.Ok(snapshot);
});
api.MapGet("/health-history", async (HealthHistoryService history, CancellationToken ct) => Results.Ok(await history.ListAsync(ct)));
api.MapGet("/maintenance", async (MaintenanceStatusService maintenance, HttpContext context) => Results.Ok(await maintenance.GetAsync(context.RequestAborted)));
api.MapGet("/server-history", async (ServerEventHistoryService history, HttpContext context) => Results.Ok(await history.ListAsync(context.RequestAborted)));
api.MapPost("/maintenance/run/{operation}", async (string operation, MaintenanceRunRequest request, BackupService backups, VersionService versions, ServerSupervisor supervisor, MaintenanceHistoryService history, NotificationService notifications, HttpContext context) =>
{
    if (!context.User.HasClaim("role", "owner") && !context.User.HasClaim("role", "admin"))
        return ApiErrors.Forbidden(context);
    if (!request.Confirm) return ApiErrors.BadRequest(context, "Explicit maintenance confirmation is required.");
    var started = DateTimeOffset.UtcNow;
    try
    {
        switch (operation.ToLowerInvariant())
        {
            case "backup":
                await backups.CreateBackupAsync("manual", context.RequestAborted);
                break;
            case "update-check":
                await versions.CheckForUpdateAsync(context.RequestAborted);
                break;
            case "restart":
                await supervisor.RestartAsync(context.RequestAborted);
                break;
            default:
                return ApiErrors.NotFound(context, $"Unknown maintenance operation '{operation}'.");
        }
        await history.RecordAsync($"manual-{operation}", true, started, DateTimeOffset.UtcNow, "Maintenance operation completed.", context.RequestAborted);
        return Results.NoContent();
    }
    catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
    {
        await history.RecordAsync($"manual-{operation}", false, started, DateTimeOffset.UtcNow, exception.Message, context.RequestAborted);
        await notifications.SendAsync($"Factorio maintenance failed ({operation})", exception.Message, context.RequestAborted);
        return ApiErrors.BadRequest(context, exception.Message, "Maintenance failed");
    }
});

api.MapGet("/saves", (DataPaths paths) => Results.Ok(Directory.EnumerateFiles(paths.Saves, "*.zip").Select(Path.GetFileName).Order()));
api.MapPost("/saves/create", async (SaveCreateRequest request, ServerSupervisor supervisor, HttpContext context) =>
{
    try { return Results.Ok(new { name = await supervisor.CreateSaveAsync(request.Name, context.RequestAborted) }); }
    catch (InvalidOperationException exception) { return ApiErrors.Create(context, StatusCodes.Status400BadRequest, exception.Message, "Save creation failed"); }
});
api.MapPost("/saves/select/{saveName}", async (string saveName, StateStore state, DataPaths paths, ServerSupervisor supervisor, HttpContext context) =>
{
    if (supervisor.IsRunning) return ApiErrors.Conflict(context, "Stop the server before switching saves.");
    var safe = Path.GetFileName(saveName);
    if (!string.Equals(safe, saveName, StringComparison.Ordinal) || !File.Exists(Path.Combine(paths.Saves, safe))) return ApiErrors.NotFound(context, $"Save '{safe}' was not found.");
    var settings = await state.GetAsync<ServerSettings>("settings", context.RequestAborted) ?? new ServerSettings();
    settings = settings with { ActiveSave = safe };
    await state.SetAsync("settings", settings, context.RequestAborted);
    return Results.Ok(settings);
});
api.MapPost("/saves/upload", async (IFormFile file, DataPaths paths, ServerSupervisor supervisor, HttpContext context) =>
{
    if (supervisor.IsRunning) return ApiErrors.Conflict(context, "Stop the server before uploading a save.");
    var safe = Path.GetFileName(file.FileName);
    if (string.IsNullOrWhiteSpace(safe) || !string.Equals(safe, file.FileName, StringComparison.Ordinal) || !safe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        return ApiErrors.BadRequest(context, "Upload a Factorio save as a file named with the .zip extension.", "Save upload failed");
    if (file.Length == 0 || file.Length > 1024L * 1024 * 1024)
        return ApiErrors.BadRequest(context, "The save upload must be non-empty and no larger than 1 GB.", "Save upload failed");
    var destination = Path.Combine(paths.Saves, safe);
    if (File.Exists(destination)) return ApiErrors.Conflict(context, $"A save named '{safe}' already exists. Choose a different name.");
    var temporary = destination + ".upload-" + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            await file.CopyToAsync(output, context.RequestAborted);
        try
        {
            using var archive = ZipFile.OpenRead(temporary);
            if (archive.Entries.Count == 0) return ApiErrors.BadRequest(context, "The uploaded zip archive is empty.", "Save upload failed");
        }
        catch (InvalidDataException) { return ApiErrors.BadRequest(context, "The uploaded file is not a valid zip archive.", "Save upload failed"); }
        try { File.Move(temporary, destination); }
        catch (IOException) when (File.Exists(destination)) { return ApiErrors.Conflict(context, $"A save named '{safe}' already exists. Choose a different name."); }
        return Results.Created($"/api/saves/{Uri.EscapeDataString(safe)}", new { name = safe });
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
});
api.MapPost("/saves/backup", async (BackupService backups, HttpContext context) =>
{
    try { return Results.Ok(new { name = await backups.CreateBackupAsync("manual", context.RequestAborted) }); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("Stop the server", StringComparison.OrdinalIgnoreCase))
    { return ApiErrors.Conflict(context, exception.Message); }
    catch (InvalidOperationException exception)
    { return ApiErrors.BadRequest(context, exception.Message, "Backup failed"); }
});
api.MapGet("/backups", async (BackupService backups, CancellationToken ct) => Results.Ok(await backups.ListAsync(ct)));
api.MapGet("/backups/{backupId}/download", async (string backupId, BackupService backups, HttpContext context) =>
{
    try { var result = await backups.OpenReadAsync(backupId, context.RequestAborted); return Results.File(result!.Value.Content, "application/zip", result.Value.Metadata.FileName); }
    catch (InvalidOperationException exception) { return ApiErrors.NotFound(context, exception.Message); }
});
api.MapPost("/backups/{backupId}/restore", async (string backupId, BackupRestoreRequest request, BackupService backups, ServerSupervisor supervisor, HttpContext context) =>
{
    if (!context.User.HasClaim("role", "owner")) return ApiErrors.Forbidden(context);
    if (supervisor.IsRunning) return ApiErrors.Conflict(context, "Stop the server before restoring a backup.");
    if (!request.Confirm) return ApiErrors.BadRequest(context, "Explicit restore confirmation is required.");
    try { await backups.RestoreAsync(backupId, true, context.RequestAborted); return Results.NoContent(); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Backup restore failed"); }
});
api.MapPatch("/backups/{backupId}", async (string backupId, BackupRenameRequest request, BackupService backups, HttpContext context) =>
{
    if (!context.User.HasClaim("role", "owner")) return ApiErrors.Forbidden(context);
    try { await backups.RenameAsync(backupId, request.Name, context.RequestAborted); return Results.NoContent(); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Backup rename failed"); }
});
api.MapDelete("/backups/{backupId}", async (string backupId, BackupService backups, HttpContext context) =>
{
    if (!context.User.HasClaim("role", "owner")) return ApiErrors.Forbidden(context);
    try { await backups.DeleteAsync(backupId, context.RequestAborted); return Results.NoContent(); }
    catch (InvalidOperationException exception) { return ApiErrors.NotFound(context, exception.Message); }
});

api.MapGet("/versions", (VersionService versions) => Results.Ok(versions.GetCachedVersions()));
api.MapGet("/versions/catalog", async (VersionService versions, HttpContext context) =>
{
    try { return (IResult)Results.Ok(await versions.GetCatalogAsync(context.RequestAborted)); }
    catch (HttpRequestException exception) { return ApiErrors.BadGateway(context, exception.Message); }
});
api.MapGet("/updates", async (VersionService versions, HttpContext context) =>
{
    var status = await versions.GetUpdateStatusAsync(context.RequestAborted);
    return status is null ? Results.Json(null) : Results.Ok(status);
});
api.MapPost("/updates/check", async (VersionService versions, HttpContext context) => Results.Ok(await versions.CheckForUpdateAsync(context.RequestAborted)));
api.MapPost("/versions/download/{channel}/{version}", async (string channel, string version, VersionService versions, HttpContext context) =>
{
    try { return (IResult)Results.Ok(await versions.DownloadAsync(channel, version, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Version download failed"); }
    catch (HttpRequestException exception) { return ApiErrors.BadGateway(context, exception.Message); }
});
api.MapPost("/versions/apply", async (VersionApplyRequest request, VersionService versions, HttpContext context) =>
{
    try { return Results.Ok(await versions.ApplyAsync(request, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Version update failed"); }
});

api.MapGet("/mods", async (ModService mods, HttpContext context) => Results.Ok(await mods.ListAsync(context.RequestAborted)));
api.MapGet("/mods/search", async (string query, ModService mods, HttpContext context) =>
{
    try { return (IResult)Results.Ok(await mods.SearchAsync(query, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Mod search failed"); }
    catch (HttpRequestException exception) { return ApiErrors.BadGateway(context, exception.Message); }
});
api.MapGet("/mods/recovery", async (ModService mods, HttpContext context) => Results.Ok(await mods.GetRecoveryStatusAsync(context.RequestAborted)));
api.MapPost("/mods/preflight", async (ModPreflightRequest request, ModService mods, HttpContext context) => Results.Ok(await mods.PreflightAsync(request, context.RequestAborted)));
api.MapPost("/mods/install", async (ModInstallRequest request, ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.InstallAsync(request, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Mod operation failed"); }
});
api.MapPost("/mods/upload", async (HttpRequest request, ModService mods, HttpContext context) =>
{
    if (!request.HasFormContentType) return ApiErrors.BadRequest(context, "Upload the mod as multipart/form-data.", "Mod upload failed");
    var form = await request.ReadFormAsync(context.RequestAborted);
    var file = form.Files.GetFile("file");
    if (file is null) return ApiErrors.BadRequest(context, "Choose a mod .zip file.", "Mod upload failed");
    if (file.Length > 512L * 1024 * 1024) return ApiErrors.BadRequest(context, "The mod archive exceeds the 512 MB limit.", "Mod upload failed");
    var enabled = !bool.TryParse(form["enabled"], out var parsedEnabled) || parsedEnabled;
    var includeDependencies = !bool.TryParse(form["includeDependencies"], out var parsedDependencies) || parsedDependencies;
    try
    {
        await using var stream = file.OpenReadStream();
        return Results.Ok(await mods.UploadAsync(stream, file.FileName, new ModUploadRequest(enabled, includeDependencies, true), context.RequestAborted));
    }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Mod upload failed"); }
});
api.MapGet("/mods/updates", async (ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.CheckUpdatesAsync(context.RequestAborted)); }
    catch (HttpRequestException exception) { return ApiErrors.BadGateway(context, $"Mod Portal update check failed: {exception.Message}"); }
});
api.MapPost("/mods/{name}/update", async (string name, ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.UpdateAsync(name, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Mod update failed"); }
    catch (HttpRequestException exception) { return ApiErrors.BadGateway(context, $"Mod Portal update failed: {exception.Message}"); }
});
api.MapPost("/mods/{name}/enabled/{enabled:bool}", async (string name, bool enabled, ModService mods, HttpContext context) =>
{
    try { return Results.Ok(await mods.SetEnabledAsync(name, enabled, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Mod operation failed"); }
});
api.MapDelete("/mods/{name}", async (string name, bool? force, ModService mods, HttpContext context) =>
{
    if (force == true && !context.User.HasClaim("role", "owner")) return ApiErrors.Forbidden(context);
    try { return Results.Ok(await mods.UninstallAsync(name, force == true, context.RequestAborted)); }
    catch (InvalidOperationException exception) { return ApiErrors.Conflict(context, exception.Message); }
});
api.MapPost("/mods/bulk-update", async (ModBulkUpdateRequest request, ModService mods, HttpContext context) =>
{
    if (!request.Confirm) return ApiErrors.BadRequest(context, "Explicit bulk update confirmation is required.");
    try { return Results.Ok(await mods.BulkUpdateAsync(request.Names ?? [], context.RequestAborted)); }
    catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message); }
});
api.MapGet("/mods/profiles", async (ModService mods, HttpContext context) => Results.Ok(await mods.ProfilesAsync(context.RequestAborted)));
api.MapPost("/mods/profiles", async (ModProfileRequest request, ModService mods, HttpContext context) => { try { return Results.Ok(await mods.SaveProfileAsync(request, context.RequestAborted)); } catch (InvalidOperationException e) { return ApiErrors.BadRequest(context, e.Message); } });
api.MapPost("/mods/profiles/{name}/apply", async (string name, ModService mods, HttpContext context) => { try { return Results.Ok(await mods.ApplyProfileAsync(name, context.RequestAborted)); } catch (InvalidOperationException e) { return ApiErrors.BadRequest(context, e.Message); } });
api.MapDelete("/mods/profiles/{name}", async (string name, ModService mods, HttpContext context) => Results.Ok(new { deleted = await mods.DeleteProfileAsync(name, context.RequestAborted) }));

api.MapGet("/players/{kind}", async (string kind, PlayerListService players, HttpContext context) => Results.Ok(await players.ListAsync(kind, context.RequestAborted)));
api.MapPost("/players/{kind}", async (string kind, PlayerListRequest request, PlayerListService players, HttpContext context) => Results.Ok(await players.AddAsync(kind, request.PlayerName, context.RequestAborted)));
api.MapDelete("/players/{kind}/{name}", async (string kind, string name, PlayerListService players, HttpContext context) => Results.Ok(await players.RemoveAsync(kind, name, context.RequestAborted)));

api.MapGet("/live-players", async (LivePlayerService players, HttpContext context) =>
{
    try { return Results.Ok(await players.ListAsync(context.RequestAborted)); }
    catch (ServerNotRunningException e) { return ApiErrors.Conflict(context, e.Message); }
    catch (InvalidOperationException e) { return ApiErrors.ServiceUnavailable(context, e.Message); }
    catch (TimeoutException e) { return ApiErrors.ServiceUnavailable(context, e.Message); }
});
api.MapPost("/live-chat", async (LiveChatRequest request, LivePlayerService players, HttpContext context) =>
{
    try { await players.ChatAsync(request.Message, context.RequestAborted); return Results.NoContent(); }
    catch (ArgumentException e) { return ApiErrors.BadRequest(context, e.Message); }
    catch (ServerNotRunningException e) { return ApiErrors.Conflict(context, e.Message); }
    catch (InvalidOperationException e) { return ApiErrors.ServiceUnavailable(context, e.Message); }
    catch (TimeoutException e) { return ApiErrors.ServiceUnavailable(context, e.Message); }
}).RequireRateLimiting("live-control");
api.MapPost("/live-players/kick", async (LivePlayerActionRequest request, LivePlayerService players, HttpContext context) =>
{
    try { await players.KickAsync(request.PlayerName, context.RequestAborted); return Results.NoContent(); }
    catch (ArgumentException e) { return ApiErrors.BadRequest(context, e.Message); }
    catch (ServerNotRunningException e) { return ApiErrors.Conflict(context, e.Message); }
    catch (InvalidOperationException e) { return ApiErrors.ServiceUnavailable(context, e.Message); }
    catch (TimeoutException e) { return ApiErrors.ServiceUnavailable(context, e.Message); }
}).RequireRateLimiting("live-control");
api.MapPost("/live-players/ban", async (LivePlayerActionRequest request, LivePlayerService players, HttpContext context) =>
{
    try { await players.BanAsync(request.PlayerName, request.Reason, context.RequestAborted); return Results.NoContent(); }
    catch (ArgumentException e) { return ApiErrors.BadRequest(context, e.Message); }
    catch (ServerNotRunningException e) { return ApiErrors.Conflict(context, e.Message); }
    catch (InvalidOperationException e) { return ApiErrors.ServiceUnavailable(context, e.Message); }
    catch (TimeoutException e) { return ApiErrors.ServiceUnavailable(context, e.Message); }
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
        return ApiErrors.Forbidden(context.HttpContext);
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
    return ApiErrors.Forbidden(context.HttpContext);
}

public partial class Program;
