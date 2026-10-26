using Azure.Messaging.ServiceBus;
using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kvytok.Api.Infrastructure;

/// <summary>
/// Sends what <see cref="OutboxPublisher"/> could not: rows whose immediate publish failed, or
/// whose replica died between the commit and the publish. Hosted here rather than in the worker
/// because the worker is woken by queue messages, and producing those is this loop's whole job.
/// It runs only while the API is up, which is enough: a stuck hold matters only to someone trying
/// to book that seat, and their request is what wakes the API and with it this loop.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopes,
    ServiceBusSender queue,
    TimeProvider clock,
    IOptions<OutboxOptions> options,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    // On a timer, not on the create path, so that a request of any kind drains it
    public static readonly TimeSpan DrainEvery = TimeSpan.FromSeconds(10);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The timer runs on the injected clock, so under a test's fake clock only the startup drain happens
        using var timer = new PeriodicTimer(DrainEvery, clock);
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Outbox drain failed; trying again in {DrainEvery}", DrainEvery);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Sends every row whose claim has lapsed, a batch at a time, until none is left or a send fails.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (await DrainBatchAsync(cancellationToken))
        {
        }
    }

    /// <returns>true if the batch was full and all of it went out, so more may be waiting.</returns>
    private async Task<bool> DrainBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KvytokDbContext>();

        // The claim is ours until the lease lapses, and nobody acts on a row they no longer own:
        // sending stops early enough that recording it still lands inside the claim. Both clocks
        // start before the claim is taken, so they run out no later than the claim does.
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        owned.CancelAfter(options.Value.OwnedFor);
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(owned.Token);
        sending.CancelAfter(options.Value.SendTimeout);

        var claimed = await ClaimAsync(db, options.Value.Lease, cancellationToken);
        if (claimed.Count == 0)
            return false;

        // One at a time, not as one batch: rows from different transactions need not travel
        // together, and one that the broker refuses should not hold back the rest. Oldest first,
        // in C# because OUTPUT returns rows in no promised order, whatever the CTE's ORDER BY says
        var sent = new List<long>(claimed.Count);
        foreach (var row in claimed.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id))
        {
            if (sending.IsCancellationRequested)
            {
                logger.LogInformation("Outbox claim lapsed after {Sent} of {Claimed} messages; the rest wait for the next drain",
                    sent.Count, claimed.Count);
                break;
            }

            try
            {
                await queue.SendMessageAsync(OutboxPublisher.ToServiceBusMessage(row), sending.Token);
                sent.Add(row.Id);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Outbox message {OutboxId} was not published; it is retried once its claim lapses",
                    row.Id);
            }
        }

        if (sent.Count > 0)
        {
            // A shutdown, or a claim that lapses, before this lands means these rows go out again;
            // the guard on PublishedAt keeps the record straight and the consumer absorbs the duplicate
            await OutboxPublisher.MarkPublishedAsync(db, sent, clock.GetUtcNow(), owned.Token);
            logger.LogInformation("Relayed {Count} owed outbox messages {OutboxIds}", sent.Count, sent);
        }

        return sent.Count == BatchSize;
    }

    /// <summary>
    /// Takes a batch no other relay holds. Each API replica runs a relay, and READPAST lets them
    /// skip each other's rows instead of queueing behind them, so their batches are disjoint and a
    /// row goes out once rather than once per replica. A claim older than the lease belongs to a
    /// relay that died mid-drain and is taken over; a row never claimed counts from its creation,
    /// which keeps the relay off rows whose own request is still sending them. Lease times are the
    /// database's: CreatedAt is set by it, and a cutoff computed on a replica would be off by that
    /// replica's clock skew. One clock, one comparison. COALESCE cannot be sought on, but the
    /// filtered index on PublishedAt IS NULL holds only owed rows, so it is evaluated over those alone.
    /// </summary>
    private static Task<List<OutboxMessage>> ClaimAsync(
        KvytokDbContext db, TimeSpan lease, CancellationToken cancellationToken) =>
        db.OutboxMessages
            .FromSql($"""
                WITH owed AS (
                    SELECT TOP ({BatchSize}) *
                    FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE PublishedAt IS NULL
                      AND COALESCE(ClaimedAt, CreatedAt) < DATEADD(millisecond, -{(int)lease.TotalMilliseconds}, SYSDATETIMEOFFSET())
                    ORDER BY CreatedAt, Id)
                UPDATE owed SET ClaimedAt = SYSDATETIMEOFFSET()
                OUTPUT inserted.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
}
