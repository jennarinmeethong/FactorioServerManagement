using FactorioManager.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"factorio-manager-test-{Guid.NewGuid():N}");

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
