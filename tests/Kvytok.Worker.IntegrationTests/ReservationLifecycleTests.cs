using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Kvytok.Domain;

namespace Kvytok.Worker.IntegrationTests;

public sealed class ReservationLifecycleTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Payment_Confirms_A_Held_Reservation_And_Books_Its_Seat()
    {
        var reservation = await database.SeedHeldAsync();

        await using var db = database.CreateDb();
        var before = (await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, NewMessageId(), Ct)).Before;

        Assert.Equal(ReservationStatus.Held, before);
        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task A_Second_Payment_Confirms_Nothing()
    {
        var reservation = await database.SeedHeldAsync();
        await using (var db = database.CreateDb())
            await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, NewMessageId(), Ct);
        var rowVersion = await RowVersionOfAsync(reservation);

        await using (var db = database.CreateDb())
        {
            var before = (await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, NewMessageId(), Ct)).Before;
            Assert.Equal(ReservationStatus.Confirmed, before);
        }

        // not merely the same status: the second delivery wrote nothing at all
        Assert.Equal(rowVersion, await RowVersionOfAsync(reservation));
        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task Expiry_Expires_An_Unpaid_Reservation_And_Frees_Its_Seat()
    {
        var reservation = await database.SeedHeldAsync();

        await using var db = database.CreateDb();
        var before = (await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, NewMessageId(), Ct)).Before;

        Assert.Equal(ReservationStatus.Held, before);
        Assert.Equal((ReservationStatus.Expired, SeatStatus.Free), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task Expiry_Leaves_A_Paid_Reservation_Alone()
    {
        var reservation = await database.SeedHeldAsync();
        await using (var db = database.CreateDb())
            await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, NewMessageId(), Ct);

        await using (var db = database.CreateDb())
        {
            var before = (await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, NewMessageId(), Ct)).Before;
            Assert.Equal(ReservationStatus.Confirmed, before);
        }

        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task A_Late_Payment_Cannot_Resurrect_An_Expired_Reservation()
    {
        var reservation = await database.SeedHeldAsync();
        await using (var db = database.CreateDb())
            await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, NewMessageId(), Ct);

        await using (var db = database.CreateDb())
        {
            var before = (await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, NewMessageId(), Ct)).Before;
            Assert.Equal(ReservationStatus.Expired, before);
        }

        Assert.Equal((ReservationStatus.Expired, SeatStatus.Free), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task A_Payment_That_Lands_Between_The_Expiry_Reading_And_Writing_Wins()
    {
        // The 9:59 case, made deterministic: the expiry has read Held and is about to write
        // Expired when the payment commits. An unconditional write would now free a paid seat.
        var reservation = await database.SeedHeldAsync();
        var (expiryId, paymentId) = await SeparatedIdsAsync(reservation);
        var payment = new BeforeFirstReservationUpdate(async () =>
        {
            await using var other = database.CreateDb();
            await DatabaseFixture.Lifecycle(other).ConfirmAsync(reservation.Id, paymentId, Ct);
        });

        await using var db = database.CreateDb(payment);
        var before = (await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, expiryId, Ct)).Before;

        Assert.Equal(ReservationStatus.Confirmed, before);
        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task An_Expiry_That_Lands_Between_The_Payment_Reading_And_Writing_Wins()
    {
        var reservation = await database.SeedHeldAsync();
        var (expiryId, paymentId) = await SeparatedIdsAsync(reservation);
        var expiry = new BeforeFirstReservationUpdate(async () =>
        {
            await using var other = database.CreateDb();
            await DatabaseFixture.Lifecycle(other).ExpireAsync(reservation.Id, expiryId, Ct);
        });

        await using var db = database.CreateDb(expiry);
        var before = (await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, paymentId, Ct)).Before;

        Assert.Equal(ReservationStatus.Expired, before);
        Assert.Equal((ReservationStatus.Expired, SeatStatus.Free), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task Payment_And_Expiry_Racing_Always_Leave_Reservation_And_Seat_In_Agreement()
    {
        var reservations = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => database.SeedHeldAsync()));

        await Task.WhenAll(reservations.SelectMany(r => new[]
        {
            Task.Run(async () => { await using var db = database.CreateDb(); await DatabaseFixture.Lifecycle(db).ConfirmAsync(r.Id, NewMessageId(), Ct); }),
            Task.Run(async () => { await using var db = database.CreateDb(); await DatabaseFixture.Lifecycle(db).ExpireAsync(r.Id, NewMessageId(), Ct); }),
        }));

        foreach (var reservation in reservations)
        {
            Assert.Contains(await database.StatusOfAsync(reservation), new[]
            {
                (ReservationStatus.Confirmed, SeatStatus.Booked),
                (ReservationStatus.Expired, SeatStatus.Free),
            });
        }
    }

    [Fact]
    public async Task Returns_Null_For_An_Unknown_Reservation()
    {
        await using var db = database.CreateDb();

        Assert.Null((await DatabaseFixture.Lifecycle(db).ConfirmAsync(Guid.NewGuid(), NewMessageId(), Ct)).Before);
        Assert.Null((await DatabaseFixture.Lifecycle(db).ExpireAsync(Guid.NewGuid(), NewMessageId(), Ct)).Before);
    }

    [Fact]
    public async Task The_Same_Message_Delivered_Twice_Is_Handled_Once()
    {
        // Expired is the case that matters: a late payment changes no row, and only the message key
        // tells its second delivery from a second payment that would need a second refund
        var reservation = await database.SeedHeldAsync();
        await using (var db = database.CreateDb())
            await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, NewMessageId(), Ct);
        var payment = NewMessageId();

        await using (var db = database.CreateDb())
            Assert.Equal(new LifecycleStep(ReservationStatus.Expired), await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, payment, Ct));
        await using (var db = database.CreateDb())
            Assert.Equal(LifecycleStep.Duplicate, await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, payment, Ct));
    }

    [Fact]
    public async Task The_Same_Message_Delivered_Twice_At_Once_Is_Handled_Once()
    {
        var reservation = await database.SeedHeldAsync();
        var payment = NewMessageId();

        var steps = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var db = database.CreateDb();
            return await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, payment, Ct);
        })));

        Assert.Single(steps, s => s == new LifecycleStep(ReservationStatus.Held));
        Assert.Single(steps, s => s == LifecycleStep.Duplicate);
        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task A_Delivery_Held_Up_By_Another_Times_Out_Then_Retries()
    {
        var reservation = await database.SeedHeldAsync();
        var payment = NewMessageId();

        // Another delivery of the same message holding the key for longer than the lock timeout
        // (plain ADO.NET: the context's retrying strategy will not hold a transaction open across calls)
        await using var other = database.CreateDb();
        var connection = other.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        var held = await connection.BeginTransactionAsync(Ct);
        await using (var hold = connection.CreateCommand())
        {
            hold.Transaction = held;
            hold.CommandText = "SELECT * FROM ProcessedMessages WITH (UPDLOCK, HOLDLOCK) WHERE MessageId = @id";
            var id = hold.CreateParameter();
            id.ParameterName = "@id";
            id.Value = payment;
            hold.Parameters.Add(id);
            await hold.ExecuteNonQueryAsync(Ct);
        }
        var release = Task.Run(async () =>
        {
            await Task.Delay(ReservationLifecycle.LockTimeoutMilliseconds + 1000, Ct);
            await held.RollbackAsync(Ct);
        });

        var keyChecks = new KeyChecks();
        await using var db = database.CreateDb(keyChecks);
        var step = await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, payment, Ct);
        await release;

        // More than one check: it gave up waiting rather than wait as long as the other did, and
        // the execution strategy took the timeout for transient and ran the step again
        Assert.True(keyChecks.Count > 1, $"The key was checked {keyChecks.Count} time(s).");
        Assert.Equal(new LifecycleStep(ReservationStatus.Held), step);
        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    private sealed class KeyChecks : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDLOCK"))
                Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }

    private static string NewMessageId() => Guid.NewGuid().ToString();

    /// <summary>
    /// The ids the Api gives a reservation's expiry and payment, with a recorded id between them.
    /// The interleaving tests run one step inside the other's open transaction, which production
    /// never does: if both unrecorded ids fell in the same gap of the index, the inner step would
    /// wait on the outer one's range lock while the outer one waits on it, until the lock timeout.
    /// The id between them puts the two range locks on different keys.
    /// </summary>
    private async Task<(string Expiry, string Payment)> SeparatedIdsAsync(Reservation reservation)
    {
        await using var db = database.CreateDb();
        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = $"{reservation.Id}:fence" });
        await db.SaveChangesAsync(Ct);
        return ($"{reservation.Id}:expiry", $"{reservation.Id}:payment:pay_123");
    }

    private async Task<byte[]> RowVersionOfAsync(Reservation reservation)
    {
        await using var db = database.CreateDb();
        return db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.RowVersion).Single();
    }
}
