using System.Net.Mail;
using Kvytok.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Api.Features.ReserveSeat;

public sealed record ReserveSeatRequest(int SeatNumber, string HolderEmail);
public sealed record ReserveSeatResponse(Guid ReservationId, DateTimeOffset ExpiresAt);

public sealed class ReserveSeatEndpoint : IEndpoint
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(10);
    private const int MaxEmailLength = 254;

    // SQL Server error numbers
    private const int DuplicateKeyInUniqueIndex = 2601;
    private const int UniqueConstraintViolation = 2627;

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/events/{eventId:guid}/reservations", Handle)
           .WithName("ReserveSeat")
           .WithTags("Reservations")
           .WithSummary("Holds a seat for a limited time. Returns 409 if it was taken first.");

    private static async Task<Results<Created<ReserveSeatResponse>, ValidationProblem, NotFound, Conflict<ProblemDetails>>> Handle(
        Guid eventId,
        ReserveSeatRequest request,
        KvytokDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        // Aspire turns on retry-on-failure, which only allows an explicit transaction
        // inside the execution strategy; each attempt runs the whole unit again.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            () => Reserve(db, eventId, request, clock.GetUtcNow(), cancellationToken));
    }

    private static async Task<Results<Created<ReserveSeatResponse>, ValidationProblem, NotFound, Conflict<ProblemDetails>>> Reserve(
        KvytokDbContext db,
        Guid eventId,
        ReserveSeatRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // a failed attempt may have left the reservation tracked as Added
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var seat = db.Seats.Where(s => s.EventId == eventId && s.Number == request.SeatNumber);

        // The race is decided here, in the first statement. SQL Server takes an update
        // lock on the row, so of any number of concurrent requests exactly one sees
        // 'Free' and flips it; the rest wait for that lock, re-read 'Held' and update nothing.
        var held = await seat
            .Where(s => s.Status == SeatStatus.Free)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SeatStatus.Held), cancellationToken);
        if (held == 0)
        {
            return await seat.AnyAsync(cancellationToken)
                ? SeatTaken(request.SeatNumber)
                : TypedResults.NotFound();
        }

        // we hold the row's exclusive lock until commit, so this cannot change under us
        var seatId = await seat.Select(s => s.Id).SingleAsync(cancellationToken);

        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            SeatId = seatId,
            HolderEmail = request.HolderEmail.Trim(),
            ExpiresAt = now + HoldDuration,
            Status = ReservationStatus.Held,
        };
        db.Reservations.Add(reservation);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: DuplicateKeyInUniqueIndex or UniqueConstraintViolation })
        {
            // the filtered unique index on SeatId: the seat was 'Free' but still had an
            // active reservation. The data is inconsistent; refuse rather than double-book.
            return SeatTaken(request.SeatNumber);
        }

        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Created($"/reservations/{reservation.Id}",
            new ReserveSeatResponse(reservation.Id, reservation.ExpiresAt));
    }

    private static Conflict<ProblemDetails> SeatTaken(int seatNumber) =>
        TypedResults.Conflict(new ProblemDetails
        {
            Title = "Seat is not available.",
            Detail = $"Seat {seatNumber} is already held or booked.",
            Status = StatusCodes.Status409Conflict,
        });

    private static Dictionary<string, string[]> Validate(ReserveSeatRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.SeatNumber < 1)
            errors[nameof(request.SeatNumber)] = ["SeatNumber must be 1 or greater."];

        var email = request.HolderEmail?.Trim();
        if (string.IsNullOrEmpty(email))
            errors[nameof(request.HolderEmail)] = ["HolderEmail is required."];
        else if (email.Length > MaxEmailLength || !MailAddress.TryCreate(email, out _))
            errors[nameof(request.HolderEmail)] = ["HolderEmail must be a valid email address."];

        return errors;
    }
}
