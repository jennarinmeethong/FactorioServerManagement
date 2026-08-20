using System.Security.Cryptography;
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
            ? ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden))
            : next(context);
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
