using Kvytok.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Api.Features.GetEvent;

public sealed record GetEventResponse(
    Guid EventId,
    string Name,
    DateTimeOffset StartsAt,
    int SeatCount,
    int FreeSeatCount,
    IReadOnlyList<SeatDto> Seats);

public sealed record SeatDto(int Number, SeatStatus Status);

public sealed class GetEventEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/events/{eventId:guid}", Handle)
           .WithName("GetEvent")
           .WithTags("Events")
           .WithSummary("Returns an event and the current status of each of its seats.");

    private static async Task<Results<Ok<GetEventResponse>, NotFound>> Handle(
        Guid eventId,
        KvytokDbContext db,
        CancellationToken cancellationToken)
    {
        var ev = await db.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Id, e.Name, e.StartsAt, e.SeatCount })
            .SingleOrDefaultAsync(cancellationToken);
        if (ev is null)
            return TypedResults.NotFound();

        // served by the clustered (EventId, Number) index, already in seat order
        var seats = await db.Seats
            .AsNoTracking()
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.Number)
            .Select(s => new SeatDto(s.Number, s.Status))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new GetEventResponse(
            ev.Id,
            ev.Name,
            ev.StartsAt,
            ev.SeatCount,
            seats.Count(s => s.Status == SeatStatus.Free),
            seats));
    }
}
