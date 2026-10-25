using Azure.Messaging.ServiceBus;
using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;

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
    ILogger<OutboxPublisher> logger)
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Never throws: the change is committed whatever happens here.</summary>
    public async Task PublishAsync(IReadOnlyCollection<OutboxMessage> messages)
    {
        if (messages.Count == 0)
            return;

        // Not the request's token: a client that hangs up after the commit is still owed the
        // publish. Bounded, so a broker outage cannot stall the response for minutes.
        using var timeout = new CancellationTokenSource(PublishTimeout);
        var ids = messages.Select(m => m.Id).ToList();

        try
        {
            // One batch to one queue is atomic: a reservation's event never goes without its expiry
            await queue.SendMessagesAsync(messages.Select(ToServiceBusMessage), timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbox messages {OutboxIds} were not published and remain owed", ids);
            return;
        }

        try
        {
            await db.OutboxMessages
                .Where(m => ids.Contains(m.Id) && m.PublishedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.PublishedAt, clock.GetUtcNow()), timeout.Token);
        }
        catch (Exception ex)
        {
            // sent but still marked owed, so it will be sent again; the message id makes that a duplicate
            logger.LogWarning(ex, "Outbox messages {OutboxIds} were published but not marked as published", ids);
        }
    }

    private static ServiceBusMessage ToServiceBusMessage(OutboxMessage row)
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
