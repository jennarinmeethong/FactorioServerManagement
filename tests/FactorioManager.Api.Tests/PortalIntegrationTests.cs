using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FactorioManager.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class PortalIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"factorio-manager-portal-{Guid.NewGuid():N}");

    [Fact]
    public async Task ModSearchUsesPortalSearchPostAndActiveVersion()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetAsync("settings", new ServerSettings(ActiveVersion: "2.0.77"));
        var handler = new RecordingHandler(_ => JsonResponse("{\"results\":[{\"name\":\"quality\",\"title\":\"Quality\"}],\"pagination\":{\"count\":1}}"));
        var service = CreateModService(paths, store, handler);

        var result = await service.SearchAsync("quality", CancellationToken.None);

        Assert.Equal(JsonValueKind.Array, result.GetProperty("results").ValueKind);
        Assert.Equal("quality", handler.LastRequest!.RequestUri!.AbsolutePath is "/api/search" ? handler.LastBody!.RootElement.GetProperty("query").GetString() : null);
        Assert.Equal("2.0.77", handler.LastBody!.RootElement.GetProperty("version").GetString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
    }

    [Fact]
    public async Task ModInstallResolvesRelativeDownloadUrlAndAddsCredentials()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetAsync("settings", new ServerSettings(ActiveVersion: "2.0.77"));
        var secrets = new SecretStore(paths);
        await secrets.WriteAsync(new SecretSettings("portal-user", "portal-token"));
        var archive = await CreateModArchiveAsync();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/api/mods/", StringComparison.Ordinal)
            ? JsonResponse("{\"latest_release\":{\"version\":\"1.0.0\",\"download_url\":\"/download/example-mod_1.0.0.zip\"}}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var service = CreateModService(paths, store, handler, secrets);

        var installed = await service.InstallAsync(new ModInstallRequest("example-mod"), CancellationToken.None);

        Assert.Equal("example-mod", installed.Name);
        Assert.Equal("1.0.0", installed.Version);
        Assert.Equal("/download/example-mod_1.0.0.zip", handler.LastDownloadRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("username=portal-user", handler.LastDownloadRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("token=portal-token", handler.LastDownloadRequest.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionCatalogIsReturnedFromFactorioEndpoint()
    {
        var paths = CreatePaths();
        paths.EnsureCreated();
        var store = new StateStore(paths);
        await store.InitializeAsync(CancellationToken.None);
        var handler = new RecordingHandler(_ => JsonResponse("{\"stable\":{\"headless\":\"2.0.77\"},\"experimental\":{\"headless\":\"2.1.14\"}}"));
        var service = new VersionService(paths, new SecretStore(paths), store, new ServerSupervisor(paths, store, null!, null!), new BackupService(paths, store), new TestHttpClientFactory(handler), NullLogger<VersionService>.Instance);

        var catalog = await service.GetCatalogAsync(CancellationToken.None);

        Assert.Equal("2.0.77", catalog.GetProperty("stable").GetProperty("headless").GetString());
        Assert.Equal("2.1.14", catalog.GetProperty("experimental").GetProperty("headless").GetString());
    }

    private ModService CreateModService(DataPaths paths, StateStore store, RecordingHandler handler, SecretStore? secrets = null) =>
        new(paths, store, new TestHttpClientFactory(handler), new ServerSupervisor(paths, store, null!, null!), null, secrets);

    private DataPaths CreatePaths() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataRoot"] = root }).Build());

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task<byte[]> CreateModArchiveAsync()
    {
        await using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("example-mod/info.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("{\"name\":\"example-mod\",\"version\":\"1.0.0\",\"factorio_version\":\"2.0\",\"dependencies\":[]}");
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public JsonDocument? LastBody { get; private set; }
        public HttpRequestMessage? LastDownloadRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Method == HttpMethod.Post)
            {
                LastBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            }
            if (request.RequestUri!.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal)) LastDownloadRequest = request;
            return responder(request);
        }
    }
}
