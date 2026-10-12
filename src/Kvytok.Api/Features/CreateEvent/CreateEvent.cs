using Microsoft.AspNetCore.Http.HttpResults;

namespace Kvytok.Api.Features.CreateEvent;

public sealed record CreateEventRequest(string Name, DateTimeOffset StartsAt, int SeatCount);
public sealed record CreateEventResponse(Guid EventId);

public sealed class CreateEventEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/events", Handle)
           .WithName("CreateEvent")
           .WithTags("Events")
           .WithSummary("Creates an event and its seats.");

    private static Task<Results<Created<CreateEventResponse>, ValidationProblem>> Handle(
        CreateEventRequest request,
        CancellationToken cancellationToken)
    {
        // TODO: persist the event, generate SeatCount seat rows.
        var id = Guid.CreateVersion7();
        return Task.FromResult<Results<Created<CreateEventResponse>, ValidationProblem>>(
            TypedResults.Created($"/events/{id}", new CreateEventResponse(id)));
    }
}