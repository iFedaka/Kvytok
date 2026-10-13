using Microsoft.EntityFrameworkCore;

namespace Kvytok.Domain;

public sealed class KvytokDbContext(DbContextOptions<KvytokDbContext> options) : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Event>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        model.Entity<Seat>(e =>
        {
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId);
            e.HasKey(x => x.Id).IsClustered(false);
            e.HasIndex(x => new { x.EventId, x.Number }).IsUnique().IsClustered();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            // no concurrency token: the hold is guarded by WHERE Status = 'Free'
        });

        model.Entity<Reservation>(e =>
        {
            e.HasOne<Seat>().WithMany().HasForeignKey(x => x.SeatId);

            e.HasIndex(x => x.SeatId)
                .IsUnique()
                .HasFilter("[Status] <> 'Cancelled' AND [Status] <> 'Expired'");

            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            // confirm / cancel / expire race here
            e.Property(x => x.RowVersion).IsRowVersion();
            // the worker scans by this
            e.HasIndex(x => x.ExpiresAt).HasFilter("[Status] = 'Held'");
        });
    }
}