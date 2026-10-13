using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Kvytok.Api.Infrastructure;

/// <summary>
/// Gives date-time schemas an ISO 8601 example so API clients (Scalar) pre-fill a value the JSON binder accepts.
/// </summary>
public sealed class DateTimeExampleSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
            schema.Example = JsonValue.Create("2026-12-01T17:00:00Z");

        return Task.CompletedTask;
    }
}
