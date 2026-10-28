using Microsoft.EntityFrameworkCore;

namespace Kvytok.Domain;

public sealed class KvytokDbContext(DbContextOptions<KvytokDbContext> options)
    : DbContext(KvytokDbContextOptions.RequireInterceptors(options))
{
    // status enums are stored by name; wide enough for the longest member
    private const int StatusMaxLength = 16;

    public DbSet<Event> Events => Set<Event>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Event>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Event.MaxNameLength).IsRequired();
        });

        model.Entity<Seat>(e =>
        {
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId);
            e.HasKey(x => x.Id).IsClustered(false);
            e.HasIndex(x => new { x.EventId, x.Number }).IsUnique().IsClustered();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(StatusMaxLength);
            // no concurrency token: the hold is guarded by WHERE Status = 'Free'
        });

        model.Entity<Reservation>(e =>
        {
            e.HasOne<Seat>().WithMany().HasForeignKey(x => x.SeatId);

            e.HasIndex(x => x.SeatId)
                .IsUnique()
                .HasFilter("[Status] <> 'Cancelled' AND [Status] <> 'Expired'");

            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(StatusMaxLength);
            // confirm / cancel / expire race here
            e.Property(x => x.RowVersion).IsRowVersion();
            // the worker scans by this
            e.HasIndex(x => x.ExpiresAt).HasFilter("[Status] = 'Held'");
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.Property(x => x.MessageId).HasMaxLength(OutboxMessage.MaxMessageIdLength).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(OutboxMessage.MaxSubjectLength).IsRequired();
            e.Property(x => x.Body).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSDATETIMEOFFSET()");
            // What is still owed stays small however large the table grows, so the relay's
            // claim reads only owed rows and sent ones can be kept as a record of what went out
            e.HasIndex(x => x.CreatedAt).HasFilter("[PublishedAt] IS NULL");
        });

        model.Entity<ProcessedMessage>(e =>
        {
            // the key itself is the duplicate check: a second insert of one id fails
            e.HasKey(x => x.MessageId);
            e.Property(x => x.MessageId).HasMaxLength(OutboxMessage.MaxMessageIdLength);
            e.Property(x => x.ProcessedAt).HasDefaultValueSql("SYSDATETIMEOFFSET()");
        });
    }
}