using System.Security.Cryptography;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace FactorioManager.Api;

public static class CsrfFilter
{
    public static ValueTask<object?> Validate(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
            return next(context);
        var expected = context.HttpContext.User.FindFirst("csrf")?.Value;
        var actual = request.Headers["X-CSRF-Token"].ToString();
        return string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual))
            ? ValueTask.FromResult<object?>(ApiErrors.Forbidden(context.HttpContext, "The request is missing a valid CSRF token. Refresh the page and try again."))
            : next(context);
    }
}

/// <summary>Requires an owner or admin for API mutations and records every outcome.</summary>
public static class ApiMutationAuthorizationFilter
{
    public static async ValueTask<object?> Validate(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
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
            var status = (result as IStatusCodeHttpResult)?.StatusCode;
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
}

public sealed class SetupCodeService(StateStore state)
{
    public string Code { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
        await state.GetAsync<string>("admin_password", cancellationToken) is not null;

    public async Task<bool> TryConfigureAsync(string code, string password, CancellationToken cancellationToken = default)
    {
        if (await IsConfiguredAsync(cancellationToken) || !string.Equals(code, Code, StringComparison.Ordinal) || password.Length < 8)
            return false;
        await state.SetAsync("admin_password", BCrypt.Net.BCrypt.HashPassword(password), cancellationToken);
        return true;
    }

    public async Task<bool> VerifyPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        var hash = await state.GetAsync<string>("admin_password", cancellationToken);
        return hash is not null && BCrypt.Net.BCrypt.Verify(password, hash);
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (newPassword.Length < 8 || !await VerifyPasswordAsync(currentPassword, cancellationToken)) return false;
        await state.SetAsync("admin_password", BCrypt.Net.BCrypt.HashPassword(newPassword), cancellationToken);
        return true;
    }
}
