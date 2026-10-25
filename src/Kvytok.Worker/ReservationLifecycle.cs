using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Worker;

/// <summary>
/// The two ways out of Held: confirmed by a payment, or expired when the hold runs out.
/// They race, and "set Expired where Id = x" would release a seat someone has just paid for,
/// so each is a conditional write on the reservation's rowversion. Whichever commits first
/// decides; the other finds the row changed, reads it again and sees it is too late.
/// </summary>
public sealed class ReservationLifecycle(KvytokDbContext db, ILogger<ReservationLifecycle> logger)
{
    // A conflict means the reservation left Held, and nothing moves it out of a final status,
    // so the second attempt always settles it. The bound is only a guard.
    private const int MaxAttempts = 3;

    public Task<ReservationStatus?> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken) =>
        LeaveHeldAsync(reservationId, ReservationStatus.Confirmed, SeatStatus.Booked, cancellationToken);

    public Task<ReservationStatus?> ExpireAsync(Guid reservationId, CancellationToken cancellationToken) =>
        LeaveHeldAsync(reservationId, ReservationStatus.Expired, SeatStatus.Free, cancellationToken);

    /// <returns>
    /// The status the reservation had when the decision was made: <see cref="ReservationStatus.Held"/>
    /// if this call moved it, otherwise the status it was already in, or null if there is no such reservation.
    /// </returns>
    private async Task<ReservationStatus?> LeaveHeldAsync(
        Guid reservationId, ReservationStatus to, SeatStatus seatTo, CancellationToken cancellationToken)
    {
        // Aspire turns on retry-on-failure, which only allows an explicit transaction
        // inside the execution strategy; each attempt runs the whole unit again.
        var strategy = db.Database.CreateExecutionStrategy();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await strategy.ExecuteAsync(
                    () => TryLeaveHeldAsync(reservationId, to, seatTo, cancellationToken));
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogInformation("Reservation {ReservationId} changed while being moved to {Status}; reading it again",
                    reservationId, to);
            }
        }
    }

    private async Task<ReservationStatus?> TryLeaveHeldAsync(
        Guid reservationId, ReservationStatus to, SeatStatus seatTo, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await db.Reservations.SingleOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
        if (reservation is null)
            return null;
        if (reservation.Status != ReservationStatus.Held)
            return reservation.Status;

        // UPDATE ... WHERE Id = @id AND RowVersion = @read. If anyone wrote the row since the read
        // above, nothing matches and EF throws DbUpdateConcurrencyException; the transaction rolls back.
        reservation.Status = to;
        await db.SaveChangesAsync(cancellationToken);

        // Only the winner gets here, and it holds the reservation row's lock until commit,
        // so the seat follows the reservation and never moves on its own.
        var seatMoved = await db.Seats
            .Where(s => s.Id == reservation.SeatId && s.Status == SeatStatus.Held)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, seatTo), cancellationToken);
        if (seatMoved == 0)
        {
            // A held reservation on a seat that is not held: refuse rather than book or free it blindly
            throw new InvalidOperationException(
                $"Reservation {reservationId} is Held but its seat {reservation.SeatId} is not.");
        }

        await transaction.CommitAsync(cancellationToken);
        return ReservationStatus.Held;
    }
}
