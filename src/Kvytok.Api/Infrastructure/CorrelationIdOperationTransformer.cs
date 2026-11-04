using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Kvytok.Api.Infrastructure;

/// <summary>
/// Documents <see cref="CorrelationIdMiddleware"/> on every operation: the header a caller may send,
/// and the header every response carries back, so the contract is on the Scalar page and not only in code.
/// </summary>
public sealed class CorrelationIdOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = CorrelationIdMiddleware.Header,
            In = ParameterLocation.Header,
            Required = false,
            Description = "Your id for this request, echoed back on the response and written onto every log it causes. " +
                          "A value not matching the pattern is replaced, as is a missing one.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = CorrelationIdMiddleware.Pattern },
        });

        foreach (var response in (operation.Responses ?? []).Values.OfType<OpenApiResponse>())
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers[CorrelationIdMiddleware.Header] = new OpenApiHeader
            {
                Description = "The request's correlation id: the one you sent if it was usable, otherwise the request's " +
                              "trace id. Quote it when reporting a problem. Problem bodies written by the framework " +
                              "carry it as 'correlationId' too; the header is on every response.",
                Required = true,
                // A caller's id passed it; a trace id or GUID in hex satisfies it too
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = CorrelationIdMiddleware.Pattern },
            };
        }

        return Task.CompletedTask;
    }
}
