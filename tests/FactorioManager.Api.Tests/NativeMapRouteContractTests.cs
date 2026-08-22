using FactorioManager.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class NativeMapRouteContractTests
{
    [Fact]
    public void NativeRoutesRemainInsideTheAuthenticatedCsrfAndAuditedAdminOrOwnerBoundary()
    {
        var routes = NativeMapRoutes.Contract;

        Assert.Equal("/api", NativeMapRoutes.AuthenticatedApiGroupPrefix);
        Assert.Collection(routes,
            route => AssertRoute(route, "GET", "/saves/native-readiness", requiresAdminOrOwner: false),
            route => AssertRoute(route, "POST", "/saves/exchange/import", requiresAdminOrOwner: true),
            route => AssertRoute(route, "POST", "/saves/exchange/export", requiresAdminOrOwner: true),
            route => AssertRoute(route, "POST", "/saves/preview", requiresAdminOrOwner: true));
    }

    [Fact]
    public void NativeRouteMappingUsesTheAuthenticatedApiGroupWithoutNeedingATestServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        // Route metadata construction needs to recognize these as DI services;
        // the factories are never resolved because this test does not execute HTTP.
        builder.Services.AddSingleton<NativeMapExchangeService>(_ => null!);
        builder.Services.AddSingleton<StateStore>(_ => null!);
        var app = builder.Build();
        NativeMapRoutes.Map(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        foreach (var route in NativeMapRoutes.Contract)
        {
            var endpoint = Assert.Single(endpoints, candidate => candidate.RoutePattern.RawText == $"/api{route.Pattern}");
            Assert.Contains(endpoint.Metadata.OfType<IAuthorizeData>(), metadata => string.IsNullOrEmpty(metadata.Policy) && string.IsNullOrEmpty(metadata.Roles));
        }
    }

    [Fact]
    public async Task NativePostWithInvalidCsrfIsDeniedAndAuditedByTheMappedRouteBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"factorio-native-csrf-audit-{Guid.NewGuid():N}");
        try
        {
            var paths = new DataPaths(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataRoot"] = root })
                .Build());
            var state = new StateStore(paths);
            await state.InitializeAsync(CancellationToken.None);

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(state);
            builder.Services.AddSingleton<AuditService>();
            // The handler service is resolved during minimal-API binding, but
            // CSRF must deny the request before any native operation is invoked.
            builder.Services.AddSingleton(_ => new NativeMapExchangeService(
                paths,
                state,
                new ServerSupervisor(paths, state, null!, NullLogger<ServerSupervisor>.Instance),
                new MapControlCatalogService(paths),
                new NativeRuntimeDetector(paths),
                new NativeProcessRunner(),
                new SourceRconClient()));
            var app = builder.Build();
            NativeMapRoutes.Map(app);

            var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(), candidate => candidate.RoutePattern.RawText == "/api/saves/exchange/import");
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/saves/exchange/import";
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"exchange\":\"ignored\",\"saveName\":\"ignored.zip\"}"));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("uid", "owner-1"), new Claim("role", "owner"), new Claim("csrf", "expected-token")],
                "test"));

            await endpoint.RequestDelegate!(context);

            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            var audit = await app.Services.GetRequiredService<AuditService>().ListAsync(null, 10, CancellationToken.None);
            var entry = Assert.Single(audit);
            Assert.Equal("POST /api/saves/exchange/import", entry.Action);
            Assert.Equal("denied", entry.Outcome);
            Assert.Equal("owner-1", entry.ActorUserId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertRoute(NativeMapRouteContract route, string method, string pattern, bool requiresAdminOrOwner)
    {
        Assert.Equal(method, route.Method);
        Assert.Equal(pattern, route.Pattern);
        Assert.True(route.RequiresAuthenticatedApiGroup);
        Assert.True(route.AppliesCsrfMutationFilter);
        Assert.Equal(requiresAdminOrOwner, route.RequiresAdminOrOwnerAuditAuthorization);
    }
}
