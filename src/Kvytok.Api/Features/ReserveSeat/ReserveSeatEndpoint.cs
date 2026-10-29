using System.Net.Mail;
using Kvytok.Domain;
using Kvytok.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kvytok.Api.Features.ReserveSeat;

public sealed record ReserveSeatRequest(int SeatNumber, string HolderEmail);
public sealed record ReserveSeatResponse(Guid ReservationId, DateTimeOffset ExpiresAt);

public sealed class ReserveSeatEndpoint : IEndpoint
{
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
        IOptions<ReservationOptions> options,
        OutboxPublisher outbox,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        // Aspire turns on retry-on-failure, which only allows an explicit transaction
        // inside the execution strategy; each attempt runs the whole unit again.
        var strategy = db.Database.CreateExecutionStrategy();
        var (result, owed) = await strategy.ExecuteAsync(
            () => Reserve(db, eventId, request, clock.GetUtcNow() + options.Value.HoldFor, cancellationToken));

        // Published outside the strategy so a retried attempt can never send twice. The hold is
        // committed by now, so a failed send is not an error: the outbox still owes the messages.
        await outbox.PublishAsync(owed);

        return result;
    }

    private static async Task<(Results<Created<ReserveSeatResponse>, ValidationProblem, NotFound, Conflict<ProblemDetails>> Result, IReadOnlyList<OutboxMessage> Owed)> Reserve(
        KvytokDbContext db,
        Guid eventId,
        ReserveSeatRequest request,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        // a failed attempt may have left the reservation tracked as Added
        db.ChangeTracker.Clear();

        var seat = db.Seats.Where(s => s.EventId == eventId && s.Number == request.SeatNumber);

        // Most requests for a contested seat arrive after it is taken. Under read committed
        // snapshot this read takes no lock and waits for none, so they get their 409 here
        // instead of queueing one by one for the row's update lock. It only turns requests
        // away: a seat read as 'Free' still has to win the update below.
        var status = await seat.Select(s => (SeatStatus?)s.Status).SingleOrDefaultAsync(cancellationToken);
        if (status is null)
            return (TypedResults.NotFound(), []);
        if (status != SeatStatus.Free)
            return (SeatTaken(request.SeatNumber), []);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The race is decided here. SQL Server takes an update lock on the row, so of any
        // number of concurrent requests that read 'Free' exactly one flips it; the rest wait
        // for that lock, re-read 'Held' and update nothing. Seats are never deleted, so the
        // seat read above still exists.
        var held = await seat
            .Where(s => s.Status == SeatStatus.Free)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SeatStatus.Held), cancellationToken);
        if (held == 0)
            return (SeatTaken(request.SeatNumber), []);

        // we hold the row's exclusive lock until commit, so this cannot change under us
        var seatId = await seat.Select(s => s.Id).SingleAsync(cancellationToken);

        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            SeatId = seatId,
            HolderEmail = request.HolderEmail.Trim(),
            ExpiresAt = expiresAt,
            Status = ReservationStatus.Held,
        };
        reservation.Publish(
            new ReservationCreated(reservation.Id, eventId, seatId, request.SeatNumber, reservation.ExpiresAt),
            reservation.Id.ToString());
        // The broker keeps this one invisible until the hold runs out, so expiry needs no timer
        // or sweep of its own. Due at ExpiresAt, an absolute time: published late, it is simply
        // already due, where a delay would hold the seat for that delay after the late publish.
        reservation.Publish(new ReservationExpiryDue(reservation.Id), $"{reservation.Id}:expiry",
            deliverAt: reservation.ExpiresAt);
        db.Reservations.Add(reservation);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: DuplicateKeyInUniqueIndex or UniqueConstraintViolation })
        {
            // the filtered unique index on SeatId: the seat was 'Free' but still had an
            // active reservation. The data is inconsistent; refuse rather than double-book.
            return (SeatTaken(request.SeatNumber), []);
        }

        await transaction.CommitAsync(cancellationToken);

        // the outbox rows that SaveChanges wrote alongside the reservation, ids now assigned
        var owed = db.ChangeTracker.Entries<OutboxMessage>().Select(e => e.Entity).ToList();

        return (TypedResults.Created($"/reservations/{reservation.Id}",
                new ReserveSeatResponse(reservation.Id, reservation.ExpiresAt)),
            owed);
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
