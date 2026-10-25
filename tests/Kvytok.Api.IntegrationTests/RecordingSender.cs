using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;

namespace Kvytok.Api.IntegrationTests;

/// <summary>Stands in for the reservations queue; keeps every message the API sends.</summary>
public sealed class RecordingSender : ServiceBusSender
{
    private readonly ConcurrentQueue<ServiceBusMessage> _sent = new();
    private readonly ConcurrentBag<Func<ServiceBusMessage, bool>> _failures = [];

    public IReadOnlyCollection<ServiceBusMessage> Sent => _sent;

    public override string EntityPath => "reservations";

    /// <summary>
    /// Rejects any send that includes a matching message, as an unreachable broker would.
    /// A predicate rather than a switch, since the sender is shared by tests running in parallel.
    /// </summary>
    public void FailWhen(Func<ServiceBusMessage, bool> predicate) => _failures.Add(predicate);

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default) =>
        SendMessagesAsync([message], cancellationToken);

    public override Task SendMessagesAsync(IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken = default)
    {
        var batch = messages.ToList();
        if (batch.Any(m => _failures.Any(fails => fails(m))))
            throw new ServiceBusException("Broker unavailable (test)", ServiceBusFailureReason.ServiceCommunicationProblem);

        foreach (var message in batch)
            _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
