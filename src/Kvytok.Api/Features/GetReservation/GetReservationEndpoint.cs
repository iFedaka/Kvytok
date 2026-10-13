using Kvytok.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Api.Features.GetReservation;

public sealed record GetReservationResponse(
    Guid ReservationId,
    Guid EventId,
    int SeatNumber,
    ReservationStatus Status,
    DateTimeOffset ExpiresAt);

public sealed class GetReservationEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/reservations/{reservationId:guid}", Handle)
           .WithName("GetReservation")
           .WithTags("Reservations")
           .WithSummary("Returns a reservation and its current status.");

    private static async Task<Results<Ok<GetReservationResponse>, NotFound>> Handle(
        Guid reservationId,
        KvytokDbContext db,
        CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == reservationId)
            .Join(db.Seats, r => r.SeatId, s => s.Id, (r, s) => new GetReservationResponse(
                r.Id,
                s.EventId,
                s.Number,
                r.Status,
                r.ExpiresAt))
            .SingleOrDefaultAsync(cancellationToken);

        return reservation is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(reservation);
    }
}
