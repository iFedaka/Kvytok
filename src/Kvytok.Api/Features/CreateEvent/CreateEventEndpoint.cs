using Kvytok.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kvytok.Api.Features.CreateEvent;

public sealed record CreateEventRequest(string Name, DateTimeOffset StartsAt, int SeatCount);
public sealed record CreateEventResponse(Guid EventId);

public sealed class CreateEventEndpoint : IEndpoint
{
    private const int MaxSeatCount = 10_000;

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/events", Handle)
           .WithName("CreateEvent")
           .WithTags("Events")
           .WithSummary("Creates an event and its seats.");

    private static async Task<Results<Created<CreateEventResponse>, ValidationProblem>> Handle(
        CreateEventRequest request,
        KvytokDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request, clock.GetUtcNow());
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var ev = new Event
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            StartsAt = request.StartsAt,
            SeatCount = request.SeatCount,
        };
        db.Events.Add(ev);
        db.Seats.AddRange(Enumerable.Range(1, request.SeatCount).Select(number => new Seat
        {
            Id = Guid.CreateVersion7(),
            EventId = ev.Id,
            Number = number,
            Status = SeatStatus.Free,
        }));

        // one implicit transaction: the event and all its seats, or nothing
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/events/{ev.Id}", new CreateEventResponse(ev.Id));
    }

    private static Dictionary<string, string[]> Validate(CreateEventRequest request, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors[nameof(request.Name)] = ["Name is required."];
        else if (request.Name.Trim().Length > Event.MaxNameLength)
            errors[nameof(request.Name)] = [$"Name must be at most {Event.MaxNameLength} characters."];

        if (request.StartsAt <= now)
            errors[nameof(request.StartsAt)] = ["StartsAt must be in the future."];

        if (request.SeatCount is < 1 or > MaxSeatCount)
            errors[nameof(request.SeatCount)] = [$"SeatCount must be between 1 and {MaxSeatCount}."];

        return errors;
    }
}
