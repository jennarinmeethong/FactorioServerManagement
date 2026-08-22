using FactorioManager.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"factorio-manager-test-{Guid.NewGuid():N}");

    [Fact]
    public void VersionApplyRequiresExplicitConfirmation()
    {
        var request = new VersionApplyRequest("2.0.77", "stable");

        var exception = Assert.Throws<InvalidOperationException>(() => VersionService.ValidateApplyRequest(request));

        Assert.Contains("Explicit confirmation is required", exception.Message);
    }

    [Fact]
    public void ConfirmedVersionApplyRequestPassesValidation()
    {
        var request = new VersionApplyRequest("2.0.77", "stable", Confirm: true);

        VersionService.ValidateApplyRequest(request);
    }

    [Fact]
    public async Task SetupCodeCanOnlyConfigureOneAdminAccount()
    {
        var store = await CreateStoreAsync();
        var setup = new SetupCodeService(store);

        Assert.True(await setup.TryConfigureAsync(setup.Code, "this-is-a-safe-password"));
        Assert.True(await setup.IsConfiguredAsync());
        Assert.True(await setup.VerifyPasswordAsync("this-is-a-safe-password"));
        Assert.False(await setup.TryConfigureAsync(setup.Code, "another-safe-password"));
        Assert.False(await setup.VerifyPasswordAsync("another-safe-password"));
    }

    [Fact]
    public async Task FreshSetupCreatesAdminAsOwner()
    {
        var store = await CreateStoreAsync();
        var setup = new SetupCodeService(store);
        var accounts = new AccountService(store);

        // The application calls the bootstrap check before setup on a clean data
        // directory; it must be a no-op until the password hash exists.
        await accounts.EnsureInitialOwnerAsync();

        Assert.True(await setup.TryConfigureAsync(setup.Code, "this-is-a-safe-password"));

        await accounts.EnsureInitialOwnerAsync();

        var admin = await accounts.FindByUsernameAsync("admin");
        Assert.NotNull(admin);
        Assert.Equal(UserRole.Owner, admin.Role);
    }

    [Fact]
    public void ApiRolesUseLowercaseStringsAndRejectNumericValues()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApiJsonOptions.Configure(options);

        var user = new UserRecord("id", "admin", UserRole.Owner, "stamp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Contains("\"role\":\"owner\"", JsonSerializer.Serialize(user, options));
        Assert.Equal(UserRole.Admin, JsonSerializer.Deserialize<CreateUserRequest>(
            "{\"username\":\"operator\",\"password\":\"safe-pass\",\"role\":\"admin\"}", options)!.Role);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateUserRequest>(
            "{\"username\":\"operator\",\"password\":\"safe-pass\",\"role\":1}", options));
    }

    [Fact]
    public async Task PasswordPolicyAcceptsEightCharactersAndRejectsSeven()
    {
        var store = await CreateStoreAsync();
        var setup = new SetupCodeService(store);

        Assert.False(await setup.TryConfigureAsync(setup.Code, "1234567"));
        Assert.True(await setup.TryConfigureAsync(setup.Code, "12345678"));
        Assert.False(await setup.ChangePasswordAsync("12345678", "1234567"));
        Assert.True(await setup.ChangePasswordAsync("12345678", "87654321"));
    }

    [Fact]
    public async Task LegacyAdminViewerIsPromotedToOwner()
    {
        var store = await CreateStoreAsync();
        var setup = new SetupCodeService(store);
        Assert.True(await setup.TryConfigureAsync(setup.Code, "12345678"));

        var accounts = new AccountService(store);
        await accounts.CreateAsync("admin", "87654321", UserRole.Viewer);
        await accounts.EnsureInitialOwnerAsync();

        Assert.Equal(UserRole.Owner, (await accounts.FindByUsernameAsync("admin"))?.Role);
    }

    [Fact]
    public async Task BootstrapAdminViewerIsPromotedToAdminWhenAnotherOwnerExists()
    {
        var store = await CreateStoreAsync();
        var setup = new SetupCodeService(store);
        Assert.True(await setup.TryConfigureAsync(setup.Code, "12345678"));

        var accounts = new AccountService(store);
        await accounts.CreateAsync("admin", "87654321", UserRole.Viewer);
        await accounts.CreateAsync("other-owner", "87654321", UserRole.Admin);
        await using var connection = store.OpenConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        var other = await accounts.FindByUsernameAsync("other-owner");
        command.Parameters.AddWithValue("$id", other!.Id);
        command.CommandText = "UPDATE users SET role='owner' WHERE id=$id";
        await command.ExecuteNonQueryAsync();

        await accounts.EnsureInitialOwnerAsync();

        Assert.Equal(UserRole.Admin, (await accounts.FindByUsernameAsync("admin"))?.Role);
    }

    [Fact]
    public async Task SecretStoreKeepsTokenWhenUsernameIsUpdatedWithoutToken()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var secrets = new SecretStore(paths);

        await secrets.WriteAsync(new SecretSettings(" portal-user ", " portal-token "));
        await secrets.UpdateAsync("renamed-user", null);

        var saved = await secrets.ReadAsync();
        Assert.Equal("renamed-user", saved.FactorioUsername);
        Assert.Equal("portal-token", saved.FactorioToken);
    }

    [Fact]
    public void ModDependencyGraphRejectsCyclesAndMissingDependencies()
    {
        var now = DateTimeOffset.UtcNow;
        var cycle = new[] { new ModEntry("a", "1", true, ["b"], now), new ModEntry("b", "1", true, ["a"], now) };
        Assert.Throws<InvalidOperationException>(() => ModService.ValidateDependencyGraph(cycle));
        var missing = new[] { new ModEntry("a", "1", true, ["? missing >= 1"], now) };
        Assert.Throws<InvalidOperationException>(() => ModService.ValidateDependencyGraph(missing));
    }

    [Fact]
    public void ModNamesRejectTraversalAndInvalidPaths()
    {
        var method = typeof(ModService).GetMethod("ValidateName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, ["../escape"]));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, ["bad/name"]));
    }

    [Fact]
    public void ModDependencyConstraintsSupportVersionAndConflictRules()
    {
        var now = DateTimeOffset.UtcNow;
        ModService.ValidateDependencyGraph([
            new ModEntry("library", "2.1.0", true, [], now),
            new ModEntry("consumer", "1.0.0", true, ["library >= 2.0"], now)
        ]);
        Assert.Throws<InvalidOperationException>(() => ModService.ValidateDependencyGraph([
            new ModEntry("library", "1.9.0", true, [], now),
            new ModEntry("consumer", "1.0.0", true, ["library >= 2.0"], now)
        ]));
        Assert.Throws<InvalidOperationException>(() => ModService.ValidateDependencyGraph([
            new ModEntry("a", "1.0.0", true, ["! b"], now),
            new ModEntry("b", "1.0.0", true, [], now)
        ]));
    }

    [Fact]
    public async Task LocalModUploadPersistsMetadataChecksumAndManifest()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetAsync("settings", new ServerSettings(ActiveVersion: "2.0.77"), CancellationToken.None);
        await using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("example-mod/info.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("{\"name\":\"example-mod\",\"version\":\"1.2.3\",\"factorio_version\":\"2.0\",\"dependencies\":[]}");
        }
        archive.Position = 0;
        var supervisor = new ServerSupervisor(paths, store, null!, null!);
        var service = new ModService(paths, store, null!, supervisor);

        var uploaded = await service.UploadAsync(archive, "example-mod.zip", new ModUploadRequest(), CancellationToken.None);

        Assert.Equal("example-mod", uploaded.Name);
        Assert.Equal("1.2.3", uploaded.Version);
        Assert.Equal(ModSource.Local, uploaded.Source);
        Assert.NotNull(uploaded.Sha256);
        Assert.True(File.Exists(Path.Combine(paths.Mods, "example-mod_1.2.3.zip")));
        Assert.Contains("example-mod", await File.ReadAllTextAsync(Path.Combine(paths.Mods, "mod-list.json")));
    }

    [Fact]
    public async Task LocalModUploadRejectsMalformedZip()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        var supervisor = new ServerSupervisor(paths, store, null!, null!);
        var service = new ModService(paths, store, null!, supervisor);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(new MemoryStream([1, 2, 3]), "broken.zip", new ModUploadRequest(), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(paths.Mods, "*.zip"));
    }

    [Fact]
    public async Task ModTransactionRecoveryRestoresOverwrittenArchiveAndQuarantinesReplayOrphan()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        var supervisor = new ServerSupervisor(paths, store, null!, null!);
        var service = new ModService(paths, store, null!, supervisor);

        var transactionRoot = Path.Combine(paths.Mods, ".mod-transaction", "replay");
        var archiveBackupRoot = Path.Combine(transactionRoot, "archives");
        Directory.CreateDirectory(archiveBackupRoot);
        var archiveName = "example-mod_1.0.0.zip";
        var archivePath = Path.Combine(paths.Mods, archiveName);
        var backupPath = Path.Combine(archiveBackupRoot, archiveName);
        await File.WriteAllTextAsync(backupPath, "original archive");
        await File.WriteAllTextAsync(archivePath, "overwritten archive");
        var replayOrphan = Path.Combine(paths.Mods, "replay-only_1.0.0.zip");
        await File.WriteAllTextAsync(replayOrphan, "replayed archive");

        var journal = Path.Combine(paths.Mods, ".mod-transaction.json");
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new
        {
            Operation = "upload",
            StartedAt = DateTimeOffset.UtcNow,
            BackupDirectory = transactionRoot,
            ArchiveBackups = new Dictionary<string, string> { [archiveName] = backupPath },
            ModListBackup = (string?)null,
            Mods = Array.Empty<ModEntry>(),
            Profiles = Array.Empty<ModProfile>(),
            Phase = "prepared"
        }));

        await service.RecoverForTestingAsync();

        Assert.Equal("original archive", await File.ReadAllTextAsync(archivePath));
        Assert.False(File.Exists(journal));
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(paths.Mods, ".quarantine")), file => file.Contains("recovery-orphan-replay-only_1.0.0.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedModTransactionJournalIsQuarantinedForRecovery()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        var supervisor = new ServerSupervisor(paths, store, null!, null!);
        var service = new ModService(paths, store, null!, supervisor);
        var journal = Path.Combine(paths.Mods, ".mod-transaction.json");
        await File.WriteAllTextAsync(journal, "{ malformed journal");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecoverForTestingAsync());

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(journal));
        Assert.Single(Directory.EnumerateFiles(paths.Mods, ".mod-transaction.json.quarantine*"));
    }

    [Fact]
    public async Task AdminPasswordCanBeChangedWithTheCurrentPassword()
    {
        var store = await CreateStoreAsync();
        var setup = new SetupCodeService(store);

        Assert.True(await setup.TryConfigureAsync(setup.Code, "this-is-a-safe-password"));
        Assert.False(await setup.ChangePasswordAsync("wrong-password", "another-safe-password"));
        Assert.True(await setup.ChangePasswordAsync("this-is-a-safe-password", "another-safe-password"));
        Assert.True(await setup.VerifyPasswordAsync("another-safe-password"));
        Assert.False(await setup.VerifyPasswordAsync("this-is-a-safe-password"));
    }

    [Fact]
    public async Task PlayerListsArePersistentAndDeduplicateNames()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var players = new PlayerListService(paths);

        var afterAdd = await players.AddAsync("admins", "Engineer", CancellationToken.None);
        await players.AddAsync("admins", "engineer", CancellationToken.None);
        var afterRemove = await players.RemoveAsync("admins", "ENGINEER", CancellationToken.None);

        Assert.Single(afterAdd);
        Assert.Empty(await players.ListAsync("admins", CancellationToken.None));
        Assert.Empty(afterRemove);
    }

    [Fact]
    public void InvalidMapSettingsAreRejectedByValidator()
    {
        var errors = MapGenerationSettingsValidator.Validate(new MapGenerationSettings { Width = 32, ExpansionMinCooldown = 10, ExpansionMaxCooldown = 5, CliffElevationInterval = 0 });
        Assert.Contains(errors, error => error.Contains("cooldown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("Cliff", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("dimensions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServerSettingsKeepVanillaAndSpaceAgeMapProfilesSeparate()
    {
        var vanilla = new MapGenerationSettings { Seed = 101 };
        var spaceAge = new MapGenerationSettings { Seed = 202, PeacefulMode = true };
        var settings = new ServerSettings(
            MapGeneration: vanilla,
            Expansion: "space-age",
            MapGenerationProfiles: new Dictionary<string, MapGenerationSettings>
            {
                ["vanilla"] = vanilla,
                ["space-age"] = spaceAge
            });

        Assert.Equal(101, settings.MapGenerationProfiles["vanilla"].Seed);
        Assert.Equal(202, settings.MapGenerationProfiles["space-age"].Seed);
        Assert.True(settings.MapGenerationProfiles["space-age"].PeacefulMode);

        var legacy = new ServerSettings(MapGeneration: vanilla);
        Assert.Equal(vanilla.Seed, legacy.MapGenerationProfiles["vanilla"].Seed);
        Assert.Equal(vanilla.Seed, legacy.MapGenerationProfiles["space-age"].Seed);
    }

    [Fact]
    public void LegacySettingsPayloadCanBeSavedAfterProfileBackfill()
    {
        const string legacyJson = "{\"serverName\":\"My Factorio Server\",\"description\":\"Managed by Factorio Server Manager\",\"maxPlayers\":0,\"visibilityPublic\":false,\"serverPassword\":null,\"autosaveMinutes\":10,\"activeSave\":null,\"channel\":\"stable\",\"activeVersion\":\"2.0.77\",\"backupIntervalHours\":24,\"backupRetention\":7,\"timeZone\":\"UTC\",\"mapGeneration\":{\"seed\":null,\"width\":0,\"height\":0,\"water\":\"normal\",\"startingArea\":\"normal\",\"terrainSegmentation\":\"normal\",\"peacefulMode\":false,\"ironOre\":{\"frequency\":\"normal\",\"size\":\"normal\",\"richness\":\"normal\"},\"copperOre\":{\"frequency\":\"normal\",\"size\":\"normal\",\"richness\":\"normal\"},\"stone\":{\"frequency\":\"normal\",\"size\":\"normal\",\"richness\":\"normal\"},\"coal\":{\"frequency\":\"normal\",\"size\":\"normal\",\"richness\":\"normal\"},\"uraniumOre\":{\"frequency\":\"normal\",\"size\":\"normal\",\"richness\":\"normal\"},\"crudeOil\":{\"frequency\":\"normal\",\"size\":\"normal\",\"richness\":\"normal\"},\"trees\":{\"frequency\":\"normal\",\"size\":\"normal\",\"richness\":\"normal\"},\"enemyBase\":{\"frequency\":\"normal\",\"size\":\"normal\",\"richness\":\"normal\"}}}";
        var settings = JsonSerializer.Deserialize<ServerSettings>(legacyJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var profiles = new Dictionary<string, MapGenerationSettings>(settings.MapGenerationProfiles, StringComparer.OrdinalIgnoreCase)
        {
            ["vanilla"] = settings.MapGeneration,
            ["space-age"] = settings.MapGeneration
        };
        var saved = settings with { MapGenerationProfiles = profiles };

        Assert.Empty(ServerSettingsValidator.Validate(saved));
        Assert.Equal("2.0.77", saved.ActiveVersion);
        Assert.Equal(2, saved.MapGenerationProfiles.Count);
    }

    [Fact]
    public void SettingsValidationReturnsFieldSpecificErrorsInsteadOfGenericBadRequest()
    {
        var settings = new ServerSettings(
            ServerName: "",
            BackupIntervalHours: 0,
            AllowCommands: "sometimes",
            MapGenerationProfiles: new Dictionary<string, MapGenerationSettings>
            {
                ["vanilla"] = new MapGenerationSettings { Width = 32 },
                ["space-age"] = new MapGenerationSettings()
            });

        var errors = ServerSettingsValidator.Validate(settings);

        Assert.Contains("serverName", errors.Keys);
        Assert.Contains("backupIntervalHours", errors.Keys);
        Assert.Contains("allowCommands", errors.Keys);
        Assert.Contains("mapGenerationProfiles.vanilla", errors.Keys);
    }

    [Fact]
    public void LivePlayerParserIgnoresHeadersAndDeduplicatesNames()
    {
        var players = LivePlayerParser.ParsePlayers("Online players (2):\n  Engineer\n  Engineer\n  Builder");
        Assert.Equal(["Engineer", "Builder"], players.Select(p => p.Name));
    }

    [Fact]
    public void LiveChatAndPlayerNamesHaveBoundedInput()
    {
        Assert.Throws<ArgumentException>(() => LivePlayerService.ValidateMessage(new string('x', 201)));
        Assert.Throws<ArgumentException>(() => LivePlayerService.ValidateName("bad name"));
    }

    [Fact]
    public async Task RconTransportFailuresAreReportedAsUnavailable()
    {
        var client = new SourceRconClient();
        var exception = await Record.ExceptionAsync(() => client.ExecuteAsync(new RconEndpoint(1, "ephemeral"), "/players", CancellationToken.None));
        Assert.True(exception is RconUnavailableException or TimeoutException);
    }

    [Fact]
    public void GeneratedMapSettingsOmitUnsetSeedAndConvertCooldownToTicks()
    {
        var map = new MapGenerationSettings { ExpansionMinCooldown = 2, ExpansionMaxCooldown = 3 };
        using var mapGen = JsonDocument.Parse(MapGenerationSettingsJson.SerializeMapGeneration(map));
        Assert.False(mapGen.RootElement.TryGetProperty("seed", out _));
        Assert.False(mapGen.RootElement.TryGetProperty("enemy_evolution", out _));
        var cliff = mapGen.RootElement.GetProperty("cliff_settings");
        Assert.True(cliff.TryGetProperty("cliff_elevation_0", out _));
        Assert.True(cliff.TryGetProperty("cliff_elevation_interval", out _));
        Assert.Equal(JsonValueKind.Number, cliff.GetProperty("richness").ValueKind);
        Assert.Equal(0, mapGen.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(0, mapGen.RootElement.GetProperty("height").GetInt32());
        Assert.True(mapGen.RootElement.GetProperty("autoplace_controls").TryGetProperty("trees", out _));

        using var runtime = JsonDocument.Parse(MapGenerationSettingsJson.SerializeMapSettings(map));
        var expansion = runtime.RootElement.GetProperty("enemy_expansion");
        Assert.Equal(3, expansion.GetProperty("min_expansion_distance").GetInt32());
        Assert.Equal(7_200, expansion.GetProperty("min_expansion_cooldown").GetInt32());
        Assert.Equal(10_800, expansion.GetProperty("max_expansion_cooldown").GetInt32());
    }

    [Fact]
    public void MapGenerationUsesFactorioAutoplaceControlIdsAndPreservesPayloadPartition()
    {
        using var mapGen = JsonDocument.Parse(MapGenerationSettingsJson.SerializeMapGeneration(new MapGenerationSettings()));
        var controls = mapGen.RootElement.GetProperty("autoplace_controls");
        var expectedIds = new[] { "iron-ore", "copper-ore", "stone", "coal", "uranium-ore", "crude-oil", "trees", "enemy-base" };
        var actualIds = controls.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();

        Assert.Equal(expectedIds.OrderBy(name => name), actualIds);
        foreach (var invalidId in new[] { "iron_ore", "copper_ore", "uranium_ore", "crude_oil", "enemy_base" })
            Assert.False(controls.TryGetProperty(invalidId, out _));
        Assert.False(mapGen.RootElement.TryGetProperty("enemy_evolution", out _));

        using var mapSettings = JsonDocument.Parse(MapGenerationSettingsJson.SerializeMapSettings(new MapGenerationSettings()));
        Assert.False(mapSettings.RootElement.TryGetProperty("autoplace_controls", out _));
        Assert.True(mapSettings.RootElement.TryGetProperty("enemy_evolution", out _));
    }

    [Fact]
    public void SystemHealthReportsPersistentInventory()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        File.WriteAllText(paths.Database, "db");
        File.WriteAllText(Path.Combine(paths.Saves, "world.zip"), "save");
        File.WriteAllText(Path.Combine(paths.Backups, "backup.zip"), "backup");
        File.WriteAllText(Path.Combine(paths.Mods, "mod.zip"), "mod");
        Directory.CreateDirectory(Path.Combine(paths.Versions, "2.0.77"));

        var health = new SystemHealthService(paths).GetSnapshot();

        Assert.True(health.DatabasePresent);
        Assert.Equal(1, health.SaveCount);
        Assert.Equal(1, health.BackupCount);
        Assert.Equal(1, health.VersionCount);
        Assert.Equal(1, health.ModFileCount);
    }

    [Fact]
    public async Task MaintenanceStatusCalculatesNextRunsFromPersistedState()
    {
        var store = await CreateStoreAsync();
        var lastBackup = DateTimeOffset.UtcNow.AddHours(-2);
        var lastUpdate = DateTimeOffset.UtcNow.AddMinutes(-30);
        await store.SetAsync("settings", new ServerSettings(BackupIntervalHours: 6, BackupRetention: 4), CancellationToken.None);
        await store.SetAsync<DateTimeOffset?>("last_scheduled_backup", lastBackup, CancellationToken.None);
        await store.SetAsync<DateTimeOffset?>("last_update_check", lastUpdate, CancellationToken.None);

        var status = await new MaintenanceStatusService(store, new MaintenanceHistoryService(store)).GetAsync(CancellationToken.None);

        Assert.Equal(6, status.BackupIntervalHours);
        Assert.Equal(4, status.BackupRetention);
        Assert.Equal(lastBackup, status.LastScheduledBackup);
        Assert.Equal(lastBackup.AddHours(6), status.NextScheduledBackup);
        Assert.Equal(lastUpdate, status.LastUpdateCheck);
        Assert.Equal(lastUpdate.AddHours(1), status.NextUpdateCheck);
        Assert.True(status.CheckedAt >= lastUpdate);
    }

    [Fact]
    public async Task MaintenanceStatusLeavesNextRunsUnsetBeforeFirstExecution()
    {
        var store = await CreateStoreAsync();

        var status = await new MaintenanceStatusService(store, new MaintenanceHistoryService(store)).GetAsync(CancellationToken.None);

        Assert.Null(status.LastScheduledBackup);
        Assert.Null(status.NextScheduledBackup);
        Assert.Null(status.LastUpdateCheck);
        Assert.Null(status.NextUpdateCheck);
        Assert.Empty(status.RecentHistory);
    }

    [Fact]
    public async Task MaintenanceHistoryPersistsNewestEntriesAndRedactsSecrets()
    {
        var store = await CreateStoreAsync();
        var history = new MaintenanceHistoryService(store);
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);

        await history.RecordAsync("scheduled-backup", false, started, DateTimeOffset.UtcNow, "token=super-secret; backup failed");

        var status = await new MaintenanceStatusService(store, history).GetAsync(CancellationToken.None);
        var entry = Assert.Single(status.RecentHistory);
        Assert.Equal("scheduled-backup", entry.Operation);
        Assert.Equal("failure", entry.Status);
        Assert.DoesNotContain("super-secret", entry.Message);
        Assert.Contains("redacted", entry.Message);
    }

    [Fact]
    public async Task MaintenanceHistoryIsBoundedAndReturnedNewestFirst()
    {
        var store = await CreateStoreAsync();
        var history = new MaintenanceHistoryService(store);
        for (var index = 0; index < 55; index++)
            await history.RecordAsync("scheduled-update-check", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, $"attempt-{index}");

        var entries = await history.ListAsync(CancellationToken.None);
        Assert.Equal(50, entries.Count);
        Assert.Equal("attempt-54", entries[0].Message);
        Assert.DoesNotContain(entries, item => item.Message == "attempt-0");
    }

    [Fact]
    public async Task ServerEventHistoryIsBoundedNewestFirstAndRedactsSecrets()
    {
        var store = await CreateStoreAsync();
        var history = new ServerEventHistoryService(store);

        for (var index = 0; index < 55; index++)
            await history.RecordAsync("automatic-restart", "failure", DateTimeOffset.UtcNow, $"attempt-{index} token=secret-{index}");

        var entries = await history.ListAsync(CancellationToken.None);
        Assert.Equal(50, entries.Count);
        Assert.Equal("attempt-54 token=[redacted]", entries[0].Message);
        Assert.DoesNotContain(entries, item => item.Message.Contains("secret-", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, item => item.Message.StartsWith("attempt-0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerEventHistoryPersistsUnexpectedExitAndRestartOutcomeKinds()
    {
        var store = await CreateStoreAsync();
        var history = new ServerEventHistoryService(store);

        await history.RecordAsync("unexpected-exit", "failure", DateTimeOffset.UtcNow, "Factorio exited unexpectedly.");
        await history.RecordAsync("automatic-restart", "success", DateTimeOffset.UtcNow, "Automatic restart succeeded.");

        var entries = await history.ListAsync(CancellationToken.None);
        Assert.Equal(["automatic-restart", "unexpected-exit"], entries.Select(entry => entry.EventKind));
        Assert.Equal(["success", "failure"], entries.Select(entry => entry.Status));
    }

    [Fact]
    public void ManualStopExitDecisionNeverTriggersAutomaticRestart()
    {
        Assert.False(ServerSupervisor.ShouldAutoRestartAfterExit(manualStopRequested: true));
        Assert.True(ServerSupervisor.ShouldAutoRestartAfterExit(manualStopRequested: false));
    }

    [Fact]
    public async Task BackupServiceCreatesBackupAndAppliesRetention()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetAsync("settings", new ServerSettings(ActiveSave: "world.zip", BackupRetention: 1), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(paths.Saves, "world.zip"), "save-data");

        var backups = new BackupService(paths, store);
        var first = await backups.CreateBackupAsync("manual", CancellationToken.None);
        await Task.Delay(1100);
        var second = await backups.CreateBackupAsync("manual", CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(paths.Backups, second)));
        Assert.Single(Directory.EnumerateFiles(paths.Backups, "*.zip"));
        Assert.False(File.Exists(Path.Combine(paths.Backups, first)));
    }

    [Fact]
    public async Task BackupServiceWithoutActiveSaveReturnsUsefulValidationError()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetAsync("settings", new ServerSettings(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new BackupService(paths, store).CreateBackupAsync("manual", CancellationToken.None));

        Assert.Contains("backup name is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupServiceRenameDeleteAndRestoreRoundTripPreservesMetadataAndActiveSave()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetAsync("settings", new ServerSettings(ActiveSave: "world.zip", BackupRetention: 5), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(paths.Saves, "world.zip"), "before");

        var backups = new BackupService(paths, store);
        var created = await backups.CreateBackupAsync("manual", CancellationToken.None);
        await backups.RenameAsync(created, "named-backup.zip", CancellationToken.None);
        var renamed = Assert.Single(await backups.ListAsync(CancellationToken.None));
        Assert.Equal("named-backup.zip", renamed.FileName);

        await File.WriteAllTextAsync(Path.Combine(paths.Saves, "world.zip"), "changed");
        await backups.RestoreAsync(renamed.Id, true, CancellationToken.None);
        Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(paths.Saves, "world.zip")));
        Assert.Equal("world.zip", (await store.GetAsync<ServerSettings>("settings", CancellationToken.None))!.ActiveSave);

        await backups.DeleteAsync(renamed.Id, CancellationToken.None);
        Assert.Empty(await backups.ListAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(paths.Backups, "named-backup.zip")));
    }

    [Fact]
    public async Task HealthHistoryIsBoundedAndPreservesResourceSamples()
    {
        var store = await CreateStoreAsync();
        var paths = CreatePaths();
        paths.EnsureCreated();
        var healthService = new SystemHealthService(paths);
        var history = new HealthHistoryService(store);

        for (var index = 0; index < 245; index++)
            await history.RecordAsync(healthService.GetSnapshot());

        var samples = await history.ListAsync(CancellationToken.None);
        Assert.Equal(240, samples.Count);
        Assert.True(samples[^1].Timestamp >= samples[0].Timestamp);
        Assert.True(samples[^1].WorkingSetBytes > 0);
    }

    [Fact]
    public void ScheduledSettingsValidateTimeAndAllowLegacyDefaults()
    {
        var valid = ServerSettingsValidator.Validate(new ServerSettings(ScheduledRestartEnabled: true, ScheduledRestartTime: "23:45"));
        Assert.Empty(valid);

        var invalid = ServerSettingsValidator.Validate(new ServerSettings(ScheduledRestartEnabled: true, ScheduledRestartTime: "9am"));
        Assert.Contains("scheduledRestartTime", invalid.Keys);
    }

    [Fact]
    public async Task MapControlCatalogDiscoversEnabledContentAndFiveSurfaceManifest()
    {
        var paths = CreatePaths(); paths.EnsureCreated();
        var root = Path.Combine(paths.Versions, "2.0.77", "data");
        Directory.CreateDirectory(Path.Combine(root, "base", "prototypes")); Directory.CreateDirectory(Path.Combine(root, "space-age", "prototypes"));
        await File.WriteAllTextAsync(Path.Combine(root, "base", "prototypes", "autoplace-controls.lua"), "data:extend({{type=\"autoplace-control\", name=\"iron-ore\", category=\"resource\", richness=true},{type=\"autoplace-control\", name=\"enemy-base\", category=\"enemy\"}})");
        await File.WriteAllTextAsync(Path.Combine(root, "space-age", "prototypes", "autoplace-controls.lua"), "data:extend({{type=\"autoplace-control\", name=\"scrap\", category=\"resource\", richness=true},{type=\"autoplace-control\", name=\"vulcanus_volcanism\", category=\"terrain\", can_be_disabled=false},{type=\"autoplace-control\", name=\"unknown-control\", category=\"resource\"}})");
        Directory.CreateDirectory(Path.Combine(paths.Mods)); await File.WriteAllTextAsync(Path.Combine(paths.Mods, "mod-list.json"), "{\"mods\":[{\"name\":\"base\",\"enabled\":true},{\"name\":\"space-age\",\"enabled\":true}]}" );
        var catalog = await new MapControlCatalogService(paths).ResolveAsync("2.0.77", "space-age");
        Assert.Equal(["Nauvis", "Vulcanus", "Gleba", "Fulgora", "Aquilo"], catalog.Surfaces);
        Assert.Equal(["enemy-base", "iron-ore", "vulcanus_volcanism", "scrap"], catalog.Controls.Select(x => x.Id));
        Assert.Equal("Fulgora", catalog.Controls.Single(x => x.Id == "scrap").Surface);
        Assert.False(catalog.Controls.Single(x => x.Id == "vulcanus_volcanism").CanBeDisabled);
    }

    [Fact]
    public async Task MapControlCatalogExcludesManifestNamesFromNonControlTables()
    {
        var paths = CreatePaths(); paths.EnsureCreated();
        var root = Path.Combine(paths.Versions, "2.0.77", "data", "base", "prototypes");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "autoplace-controls.lua"),
            "data:extend({{type=\"resource\", name=\"iron-ore\", category=\"resource\"}})");

        var catalog = await new MapControlCatalogService(paths).ResolveAsync("2.0.77", "vanilla");

        Assert.DoesNotContain(catalog.Controls, control => control.Id == "iron-ore");
    }

    [Fact]
    public void CatalogSerializationOmitsUnknownControlsAndKeepsMapSettingsSeparate()
    {
        var catalog = new MapControlCatalog("2.0.77", "fingerprint", MapControlCatalogService.SupportedSurfaces, [new("iron-ore", "Nauvis", "resource", true, true)]);
        var map = new MapGenerationSettings { ControlOverrides = new Dictionary<string, MapControlOverride> { ["iron-ore"] = new("high", "low", "normal"), ["disabled-mod-control"] = new("high", "high", "high") } };
        using var gen = JsonDocument.Parse(MapGenerationSettingsJson.SerializeMapGeneration(MapControlCatalogService.Normalize(map, catalog), catalog));
        using var runtime = JsonDocument.Parse(MapGenerationSettingsJson.SerializeMapSettings(map));
        var normalized = MapControlCatalogService.Normalize(map, catalog);
        Assert.DoesNotContain("disabled-mod-control", normalized.ControlOverrides.Keys);
        Assert.True(gen.RootElement.GetProperty("autoplace_controls").TryGetProperty("iron-ore", out _));
        Assert.False(gen.RootElement.GetProperty("autoplace_controls").TryGetProperty("disabled-mod-control", out _));
        Assert.False(runtime.RootElement.TryGetProperty("autoplace_controls", out _));
        Assert.True(runtime.RootElement.TryGetProperty("enemy_evolution", out _));
    }

    [Fact]
    public async Task NotificationSecretsRoundTripWithoutChangingFactorioCredentials()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var secrets = new SecretStore(paths);
        await secrets.WriteAsync(new SecretSettings("factorio-user", "factorio-token", "https://discord.invalid/hook", "telegram-token", "123"));
        await secrets.UpdateAsync(null, null);

        var saved = await secrets.ReadAsync();
        Assert.Equal("factorio-user", saved.FactorioUsername);
        Assert.Equal("factorio-token", saved.FactorioToken);
        Assert.Equal("https://discord.invalid/hook", saved.DiscordWebhookUrl);
        Assert.Equal("telegram-token", saved.TelegramBotToken);
        Assert.Equal("123", saved.TelegramChatId);
    }

    [Fact]
    public async Task NotificationSecretUpdateKeepsExistingDestinationsWhenFieldsAreBlank()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var secrets = new SecretStore(paths);
        await secrets.WriteAsync(new SecretSettings("factorio-user", "factorio-token", "https://discord.invalid/hook", "telegram-token", "123"));

        await secrets.UpdateNotificationsAsync(" ", "", null);

        var saved = await secrets.ReadAsync();
        Assert.Equal("https://discord.invalid/hook", saved.DiscordWebhookUrl);
        Assert.Equal("telegram-token", saved.TelegramBotToken);
        Assert.Equal("123", saved.TelegramChatId);
    }

    private async Task<StateStore> CreateStoreAsync()
    {
        var store = new StateStore(CreatePaths());
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private DataPaths CreatePaths()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataRoot"] = _root }).Build();
        return new DataPaths(configuration);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
