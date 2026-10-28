using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Worker;

/// <summary>
/// The two ways out of Held: confirmed by a payment, or expired when the hold runs out.
/// They race, and "set Expired where Id = x" would release a seat someone has just paid for,
/// so each is a conditional write on the reservation's rowversion. Whichever commits first
/// decides; the other finds the row changed, reads it again and sees it is too late.
/// Each step is also recorded under the id of the message that asked for it, in the same transaction,
/// so the same message delivered again is recognised as already handled and decides nothing twice.
/// </summary>
public sealed class ReservationLifecycle(KvytokDbContext db, ILogger<ReservationLifecycle> logger)
{
    // A conflict means the reservation left Held, and nothing moves it out of a final status,
    // so the second attempt always settles it. The bound is only a guard.
    private const int MaxAttempts = 3;

    // A step holds its locks for milliseconds. A wait this long means something is stuck, and the
    // message is better off failing into the consumer's retry than sitting on its broker lock.
    public const int LockTimeoutMilliseconds = 2000;

    public Task<LifecycleStep> ConfirmAsync(Guid reservationId, string messageId, CancellationToken cancellationToken) =>
        LeaveHeldAsync(reservationId, messageId, ReservationStatus.Confirmed, SeatStatus.Booked, cancellationToken);

    public Task<LifecycleStep> ExpireAsync(Guid reservationId, string messageId, CancellationToken cancellationToken) =>
        LeaveHeldAsync(reservationId, messageId, ReservationStatus.Expired, SeatStatus.Free, cancellationToken);

    private async Task<LifecycleStep> LeaveHeldAsync(
        Guid reservationId, string messageId, ReservationStatus to, SeatStatus seatTo, CancellationToken cancellationToken)
    {
        // Aspire turns on retry-on-failure, which only allows an explicit transaction
        // inside the execution strategy; each attempt runs the whole unit again.
        var strategy = db.Database.CreateExecutionStrategy();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await strategy.ExecuteAsync(
                    () => TryLeaveHeldAsync(reservationId, messageId, to, seatTo, cancellationToken));
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogInformation("Reservation {ReservationId} changed while being moved to {Status}; reading it again",
                    reservationId, to);
            }
        }
    }

    private async Task<LifecycleStep> TryLeaveHeldAsync(
        Guid reservationId, string messageId, ReservationStatus to, SeatStatus seatTo, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // For the connection while the step holds it: every statement here, not only the key check
        // (raw: SET takes no parameters, and this is a constant)
        await db.Database.ExecuteSqlRawAsync($"SET LOCK_TIMEOUT {LockTimeoutMilliseconds}", cancellationToken);

        // Locks the key, present or not, until commit: a second delivery of the same message running
        // alongside waits here for this one to finish, then finds its record. It holds nothing else
        // while it waits, so it cannot deadlock; racing to insert instead would end in a key violation.
        // An id not yet recorded is locked as a range, so a different message can wait here too:
        // a timeout is no sign of a duplicate, and is retried, never skipped.
        var seen = await db.ProcessedMessages
            .FromSql($"SELECT * FROM ProcessedMessages WITH (UPDLOCK, HOLDLOCK) WHERE MessageId = {messageId}")
            .AnyAsync(cancellationToken);
        if (seen)
            return LifecycleStep.Duplicate;

        // Recorded whatever the outcome, since a step that changes nothing is still a decision:
        // a payment found too late is one refund, however often it is delivered
        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId });

        var reservation = await db.Reservations.SingleOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
        if (reservation is null || reservation.Status != ReservationStatus.Held)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LifecycleStep(reservation?.Status);
        }

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
        return new LifecycleStep(ReservationStatus.Held);
    }
}

/// <summary>What a lifecycle step found.</summary>
/// <param name="Before">
/// The status the reservation had when the decision was made: <see cref="ReservationStatus.Held"/>
/// if this step moved it, otherwise the status it was already in, or null if there is no such reservation.
/// </param>
/// <param name="IsAlreadyHandled">
/// The message was handled before, so this step decided nothing and <paramref name="Before"/> is null.
/// </param>
public readonly record struct LifecycleStep(ReservationStatus? Before, bool IsAlreadyHandled = false)
{
    public static LifecycleStep Duplicate { get; } = new(null, IsAlreadyHandled: true);
}
