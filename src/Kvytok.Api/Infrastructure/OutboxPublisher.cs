using Azure.Messaging.ServiceBus;
using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kvytok.Api.Infrastructure;

/// <summary>
/// Sends outbox rows the moment their transaction commits. The request is still running then,
/// so the happy path costs no extra latency and needs nothing to wake it. Whatever this fails
/// to send stays in the outbox with no PublishedAt, which is the record that it is still owed.
/// </summary>
public sealed class OutboxPublisher(
    ServiceBusSender queue,
    KvytokDbContext db,
    TimeProvider clock,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPublisher> logger)
{
    /// <summary>Never throws: the change is committed whatever happens here.</summary>
    public async Task PublishAsync(IReadOnlyCollection<OutboxMessage> messages)
    {
        if (messages.Count == 0)
            return;

        // Not the request's token: a client that hangs up after the commit is still owed the
        // publish. Bounded, so a broker outage cannot stall the response for minutes, and by the
        // lease too: past it the relay may have taken these rows, and two senders would race.
        // Sending and recording it share the one deadline; sending stops early to leave room.
        using var owned = new CancellationTokenSource(options.Value.OwnedFor);
        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(owned.Token);
        sendTimeout.CancelAfter(options.Value.SendTimeout);
        var ids = messages.Select(m => m.Id).ToList();

        try
        {
            // One batch to one queue is atomic: a reservation's event never goes without its expiry
            await queue.SendMessagesAsync(messages.Select(ToServiceBusMessage), sendTimeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbox messages {OutboxIds} were not published and remain owed", ids);
            return;
        }

        try
        {
            await MarkPublishedAsync(db, ids, clock.GetUtcNow(), owned.Token);
        }
        catch (Exception ex)
        {
            // sent but still marked owed, so it will be sent again; the consumer absorbs the duplicate
            logger.LogWarning(ex, "Outbox messages {OutboxIds} were published but not marked as published", ids);
        }
    }

    /// <summary>
    /// Guarded on PublishedAt, so a row two senders both published keeps the first time it went out.
    /// </summary>
    internal static Task MarkPublishedAsync(
        KvytokDbContext db, IReadOnlyCollection<long> ids, DateTimeOffset at, CancellationToken cancellationToken) =>
        db.OutboxMessages
            .Where(m => ids.Contains(m.Id) && m.PublishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.PublishedAt, at), cancellationToken);

    internal static ServiceBusMessage ToServiceBusMessage(OutboxMessage row)
    {
        var message = new ServiceBusMessage(BinaryData.FromString(row.Body))
        {
            MessageId = row.MessageId,
            Subject = row.Subject,
            ContentType = "application/json",
        };
        // Already in the past if this is a late publish; the broker then delivers it at once
        if (row.DeliverAt is { } due)
            message.ScheduledEnqueueTime = due;
        return message;
    }
}
