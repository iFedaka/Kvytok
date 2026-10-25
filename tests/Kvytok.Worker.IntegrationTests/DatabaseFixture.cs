using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(Kvytok.Worker.IntegrationTests.DatabaseFixture))]

namespace Kvytok.Worker.IntegrationTests;

/// <summary>
/// One SQL Server container for the whole run. Tests isolate themselves by seeding their own
/// event, so nothing is reset between them.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync()
    {
        await _sql.StartAsync();
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
    }

    /// <summary>A context configured as Aspire configures the worker's, retry-on-failure included.</summary>
    public KvytokDbContext CreateDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<KvytokDbContext>()
            .UseSqlServer(_sql.GetConnectionString(), sql => sql.EnableRetryOnFailure())
            .AddInterceptors(interceptors)
            .Options);

    public static ReservationLifecycle Lifecycle(KvytokDbContext db) =>
        new(db, NullLogger<ReservationLifecycle>.Instance);

    /// <summary>A seat held by a fresh reservation, as the Api leaves it.</summary>
    public async Task<Reservation> SeedHeldAsync()
    {
        await using var db = CreateDb();
        var @event = new Event { Id = Guid.CreateVersion7(), Name = "Test", StartsAt = DateTimeOffset.UtcNow.AddDays(30), SeatCount = 1 };
        var seat = new Seat { Id = Guid.CreateVersion7(), EventId = @event.Id, Number = 1, Status = SeatStatus.Held };
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            SeatId = seat.Id,
            HolderEmail = "holder@example.com",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Status = ReservationStatus.Held,
        };
        db.AddRange(@event, seat, reservation);
        await db.SaveChangesAsync();
        return reservation;
    }

    public async Task<(ReservationStatus Reservation, SeatStatus Seat)> StatusOfAsync(Reservation reservation)
    {
        await using var db = CreateDb();
        var status = await db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync();
        var seat = await db.Seats.Where(s => s.Id == reservation.SeatId).Select(s => s.Status).SingleAsync();
        return (status, seat);
    }

    public async ValueTask DisposeAsync() => await _sql.DisposeAsync();
}
