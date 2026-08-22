using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FactorioManager.Api;

/// <summary>
/// The security boundary for native Factorio work.  Keeping this contract next
/// to the mappings makes the route policy verifiable without a TestServer host.
/// </summary>
internal sealed record NativeMapRouteContract(
    string Method,
    string Pattern,
    bool RequiresAuthenticatedApiGroup,
    bool AppliesCsrfMutationFilter,
    bool RequiresAdminOrOwnerAuditAuthorization);

internal static class NativeMapRoutes
{
    internal const string AuthenticatedApiGroupPrefix = "/api";

    internal static IReadOnlyList<NativeMapRouteContract> Contract { get; } =
    [
        new(HttpMethods.Get, "/saves/native-readiness", true, true, false),
        new(HttpMethods.Post, "/saves/exchange/import", true, true, true),
        new(HttpMethods.Post, "/saves/exchange/export", true, true, true),
        new(HttpMethods.Post, "/saves/preview", true, true, true)
    ];

    /// <summary>Maps native routes beneath the authenticated API boundary and its mutation gates.</summary>
    internal static void Map(WebApplication app)
    {
        var api = app.MapGroup(AuthenticatedApiGroupPrefix)
            .RequireAuthorization()
            // The mutation audit gate must be outermost so CSRF rejections are
            // captured as denied audit events rather than returning early.
            .AddEndpointFilter(ApiMutationAuthorizationFilter.Validate)
            .AddEndpointFilter(CsrfFilter.Validate);

        api.MapGet(Contract[0].Pattern, async (NativeMapExchangeService exchange, StateStore state, HttpContext context) =>
        {
            var settings = await state.GetAsync<ServerSettings>("settings", context.RequestAborted) ?? new ServerSettings();
            var readiness = exchange.Readiness(settings);
            return Results.Ok(new { readiness.Ready, readiness.Failure, version = settings.ActiveVersion, expansion = settings.Expansion });
        });
        api.MapPost(Contract[1].Pattern, async (MapExchangeImportRequest request, NativeMapExchangeService exchange, HttpContext context) =>
        {
            try { return Results.Ok(await exchange.ImportAsync(request, context.RequestAborted)); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Stop the server", StringComparison.OrdinalIgnoreCase)) { return ApiErrors.Conflict(context, exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Map exchange import failed"); }
        });
        api.MapPost(Contract[2].Pattern, async (MapExchangeExportRequest request, NativeMapExchangeService exchange, HttpContext context) =>
        {
            try { return Results.Ok(await exchange.ExportAsync(request, context.RequestAborted)); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Stop the server", StringComparison.OrdinalIgnoreCase)) { return ApiErrors.Conflict(context, exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Map exchange export failed"); }
        });
        api.MapPost(Contract[3].Pattern, async (MapPreviewRequest request, NativeMapExchangeService exchange, HttpContext context) =>
        {
            try { return Results.Ok(await exchange.PreviewAsync(request, context.RequestAborted)); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Stop the server", StringComparison.OrdinalIgnoreCase)) { return ApiErrors.Conflict(context, exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.BadRequest(context, exception.Message, "Native map preview failed"); }
        });
    }
}
