using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace Kvytok.Api.Infrastructure;

/// <summary>
/// Turns request-binding failures (malformed JSON, values of the wrong type) into a 400 ValidationProblem
/// instead of an unhandled exception, pointing at the offending field when the JSON path is known.
/// </summary>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
            return false;

        var errors = new Dictionary<string, string[]>();
        if (badRequest.InnerException is JsonException json)
        {
            var field = string.IsNullOrEmpty(json.Path) || json.Path == "$" ? "body" : json.Path.TrimStart('$', '.');
            errors[field] = [DescribeJsonError(json)];
        }
        else
        {
            errors["body"] = [badRequest.Message];
        }

        context.Response.StatusCode = badRequest.StatusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new HttpValidationProblemDetails(errors)
            {
                Status = badRequest.StatusCode,
                Title = "The request body is invalid.",
            },
        });
    }

    private static string DescribeJsonError(JsonException json) => json.InnerException switch
    {
        FormatException f when f.Message.Contains("DateTime") =>
            "Expected an ISO 8601 date-time with offset, e.g. 2026-12-01T17:00:00Z.",
        FormatException or InvalidOperationException => "The value has the wrong type or format.",
        _ when json.Path is null => "The body is not valid JSON.",
        _ => "The value has the wrong type or format.",
    };
}
