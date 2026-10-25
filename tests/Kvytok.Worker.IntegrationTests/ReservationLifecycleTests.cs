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
        var before = await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, Ct);

        Assert.Equal(ReservationStatus.Held, before);
        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task A_Payment_Delivered_Twice_Confirms_Once()
    {
        var reservation = await database.SeedHeldAsync();
        await using (var db = database.CreateDb())
            await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, Ct);
        var rowVersion = await RowVersionOfAsync(reservation);

        await using (var db = database.CreateDb())
        {
            var before = await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, Ct);
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
        var before = await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, Ct);

        Assert.Equal(ReservationStatus.Held, before);
        Assert.Equal((ReservationStatus.Expired, SeatStatus.Free), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task Expiry_Leaves_A_Paid_Reservation_Alone()
    {
        var reservation = await database.SeedHeldAsync();
        await using (var db = database.CreateDb())
            await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, Ct);

        await using (var db = database.CreateDb())
        {
            var before = await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, Ct);
            Assert.Equal(ReservationStatus.Confirmed, before);
        }

        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task A_Late_Payment_Cannot_Resurrect_An_Expired_Reservation()
    {
        var reservation = await database.SeedHeldAsync();
        await using (var db = database.CreateDb())
            await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, Ct);

        await using (var db = database.CreateDb())
        {
            var before = await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, Ct);
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
        var payment = new BeforeFirstReservationUpdate(async () =>
        {
            await using var other = database.CreateDb();
            await DatabaseFixture.Lifecycle(other).ConfirmAsync(reservation.Id, Ct);
        });

        await using var db = database.CreateDb(payment);
        var before = await DatabaseFixture.Lifecycle(db).ExpireAsync(reservation.Id, Ct);

        Assert.Equal(ReservationStatus.Confirmed, before);
        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task An_Expiry_That_Lands_Between_The_Payment_Reading_And_Writing_Wins()
    {
        var reservation = await database.SeedHeldAsync();
        var expiry = new BeforeFirstReservationUpdate(async () =>
        {
            await using var other = database.CreateDb();
            await DatabaseFixture.Lifecycle(other).ExpireAsync(reservation.Id, Ct);
        });

        await using var db = database.CreateDb(expiry);
        var before = await DatabaseFixture.Lifecycle(db).ConfirmAsync(reservation.Id, Ct);

        Assert.Equal(ReservationStatus.Expired, before);
        Assert.Equal((ReservationStatus.Expired, SeatStatus.Free), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task Payment_And_Expiry_Racing_Always_Leave_Reservation_And_Seat_In_Agreement()
    {
        var reservations = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => database.SeedHeldAsync()));

        await Task.WhenAll(reservations.SelectMany(r => new[]
        {
            Task.Run(async () => { await using var db = database.CreateDb(); await DatabaseFixture.Lifecycle(db).ConfirmAsync(r.Id, Ct); }),
            Task.Run(async () => { await using var db = database.CreateDb(); await DatabaseFixture.Lifecycle(db).ExpireAsync(r.Id, Ct); }),
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

        Assert.Null(await DatabaseFixture.Lifecycle(db).ConfirmAsync(Guid.NewGuid(), Ct));
        Assert.Null(await DatabaseFixture.Lifecycle(db).ExpireAsync(Guid.NewGuid(), Ct));
    }

    private async Task<byte[]> RowVersionOfAsync(Reservation reservation)
    {
        await using var db = database.CreateDb();
        return db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.RowVersion).Single();
    }
}
