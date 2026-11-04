using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Kvytok.Api.Infrastructure;

/// <summary>
/// Gives every request a correlation id: the caller's <c>X-Correlation-ID</c> when it sends a usable one,
/// otherwise the request's trace id, so a caller with no id of its own still gets one that finds the trace.
/// The id goes back on the response, onto every log written during the request, and onto the request span.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string Header = "X-Correlation-ID";

    /// <summary>
    /// The ids taken from a caller: written into logs and echoed back, so only short and plain ones.
    /// Also what the OpenAPI document publishes, so the documented rule is the enforced one.
    /// ECMA-262 syntax, as JSON Schema requires.
    /// </summary>
    public const string Pattern = "^[A-Za-z0-9._:-]{1,64}$";

    [GeneratedRegex(Pattern)]
    private static partial Regex UsableId();

    private static readonly object ItemKey = new();

    public static string? Get(HttpContext context) => context.Items[ItemKey] as string;

    public async Task InvokeAsync(HttpContext context)
    {
        string id = context.Request.Headers[Header] is [{ } sent] && IsUsable(sent)
            ? sent
            : Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = id;
        Activity.Current?.SetTag("correlation.id", id);
        // Set as the response starts, not now.
        // The exception handler clears the headers before writing its problem.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }

    // The whole value must match: in .NET '$' also matches before a trailing newline, and ECMA-262 has no '\z'
    internal static bool IsUsable(string id) => UsableId().Match(id) is { Success: true } match && match.Length == id.Length;
}
