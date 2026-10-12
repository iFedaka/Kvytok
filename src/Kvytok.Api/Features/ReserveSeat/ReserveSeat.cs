using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Kvytok.Api.Features.ReserveSeat;

public sealed record ReserveSeatRequest(int SeatNumber, string HolderEmail);
public sealed record ReserveSeatResponse(Guid ReservationId, DateTimeOffset ExpiresAt);

public sealed class ReserveSeatEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/events/{eventId:guid}/reservations", Handle)
           .WithName("ReserveSeat")
           .WithTags("Reservations")
           .WithSummary("Holds a seat for a limited time. Returns 409 if it was taken first.");

    private static Task<Results<Created<ReserveSeatResponse>, NotFound, Conflict<ProblemDetails>>> Handle(
        Guid eventId,
        ReserveSeatRequest request,
        CancellationToken cancellationToken)
    {
        // TODO: optimistic concurrency on the seat row (rowversion);
        // a lost update must surface here as 409.
        var id = Guid.CreateVersion7();
        return Task.FromResult<Results<Created<ReserveSeatResponse>, NotFound, Conflict<ProblemDetails>>>(
            TypedResults.Created($"/reservations/{id}",
                new ReserveSeatResponse(id, DateTimeOffset.UtcNow.AddMinutes(10))));
    }
}