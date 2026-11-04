using Kvytok.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kvytok.Api.IntegrationTests;

/// <summary>Mapped only in tests: a request that fails the way a bug would, with an exception nothing expects.</summary>
public sealed class ThrowingEndpoint : IEndpoint
{
    public const string Path = "/test/throw";

    /// <summary>Stands in for what a real exception message can hold: SQL, server names, connection details.</summary>
    public const string Message = "Internal detail for the log, not for the caller";

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet(Path, IResult () => throw new InvalidOperationException(Message))
           .ExcludeFromDescription();
}
