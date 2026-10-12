using Microsoft.AspNetCore.Http.HttpResults;

namespace Kvytok.Api.Features.GetReservation;

public sealed record GetReservationRequest(int SeatNumber, string HolderEmail);
public sealed record GetReservationResponse(Guid ReservationId, DateTimeOffset ExpiresAt);

public sealed class GetReservation : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/events/{eventId:guid}/reservations", Handle)
           .WithName("GetReservation")
           .WithTags("Reservations");

    private static Task<Results<Ok<GetReservationResponse>, Conflict, NotFound>> Handle(
        Guid eventId, GetReservationRequest request, CancellationToken ct) =>
        throw new NotImplementedException();
}