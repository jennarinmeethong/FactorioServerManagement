using System.Text.RegularExpressions;

namespace FactorioManager.Api;

public static partial class SafeDiagnostics
{
    private const int MaxMessageLength = 4000;

    [GeneratedRegex(@"(?i)(?<key>authorization)(?<sep>\s*[=:]\s*)(?<value>[^\r\n;&,]+)", RegexOptions.Compiled)]
    private static partial Regex AuthorizationAssignmentRegex();

    [GeneratedRegex(@"(?i)(?<key>token|password|secret|setup(?:[-_\s]?code)|webhook|api[-_\s]?key|access[-_\s]?token|refresh[-_\s]?token)(?<sep>\s*[=:]\s*)(?<value>[^\s;&,]+)", RegexOptions.Compiled)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)(?<key>authorization|token|password|secret|setup(?:[-_\s]?code)|webhook|api[-_\s]?key|access[-_\s]?token|refresh[-_\s]?token)=[^&\s;#]+", RegexOptions.Compiled)]
    private static partial Regex SecretQueryRegex();

    public static string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = AuthorizationAssignmentRegex().Replace(value, match => $"{match.Groups["key"].Value}=[redacted]");
        result = SecretQueryRegex().Replace(result, match => $"{match.Groups["key"].Value}=[redacted]");
        result = SecretAssignmentRegex().Replace(result, match => $"{match.Groups["key"].Value}=[redacted]");
        return result.Length <= MaxMessageLength ? result : result[..MaxMessageLength] + "…";
    }
}

public sealed record ApiErrorResponse(
    string Title,
    int Status,
    string Detail,
    string RequestId,
    IDictionary<string, string[]>? Errors = null);

public static class ApiErrors
{
    public static IResult Create(HttpContext context, int status, string detail, string title = "Request failed", IDictionary<string, string[]>? errors = null)
        => Results.Json(new ApiErrorResponse(title, status, SafeDiagnostics.Redact(detail), RequestId(context), errors?.ToDictionary(pair => pair.Key, pair => pair.Value.Select(SafeDiagnostics.Redact).ToArray())), statusCode: status, contentType: "application/problem+json");

    public static IResult Validation(HttpContext context, IDictionary<string, string[]> errors, string detail = "One or more fields are invalid.")
        => Create(context, StatusCodes.Status400BadRequest, detail, "Validation failed", errors);

    public static IResult BadRequest(HttpContext context, string detail, string title = "Request failed")
        => Create(context, StatusCodes.Status400BadRequest, detail, title);

    public static IResult Unauthorized(HttpContext context, string detail = "Sign in is required.")
        => Create(context, StatusCodes.Status401Unauthorized, detail, "Authentication required");

    public static IResult Forbidden(HttpContext context, string detail = "You do not have permission to perform this action.")
        => Create(context, StatusCodes.Status403Forbidden, detail, "Forbidden");

    public static IResult NotFound(HttpContext context, string detail = "The requested resource was not found.")
        => Create(context, StatusCodes.Status404NotFound, detail, "Not found");

    public static IResult Conflict(HttpContext context, string detail)
        => Create(context, StatusCodes.Status409Conflict, detail, "Conflict");

    public static IResult ServiceUnavailable(HttpContext context, string detail = "The Factorio server is temporarily unavailable. Try again shortly.")
        => Create(context, StatusCodes.Status503ServiceUnavailable, detail, "Service unavailable");

    public static IResult BadGateway(HttpContext context, string detail)
        => Create(context, StatusCodes.Status502BadGateway, detail, "Upstream service failed");

    public static string RequestId(HttpContext context)
        => context.TraceIdentifier;
}
