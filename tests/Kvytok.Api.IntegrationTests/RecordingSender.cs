using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;

namespace Kvytok.Api.IntegrationTests;

/// <summary>Stands in for the reservations queue; keeps every message the API sends.</summary>
public sealed class RecordingSender : ServiceBusSender
{
    private readonly ConcurrentQueue<ServiceBusMessage> _sent = new();
    private readonly ConcurrentBag<Func<ServiceBusMessage, bool>> _failures = [];
    private readonly ConcurrentBag<(Func<ServiceBusMessage, bool> Matches, TimeSpan For)> _stalls = [];

    public IReadOnlyCollection<ServiceBusMessage> Sent => _sent;

    public override string EntityPath => "reservations";

    /// <summary>
    /// Rejects any send that includes a matching message, as an unreachable broker would.
    /// A predicate rather than a switch, since the sender is shared by tests running in parallel.
    /// </summary>
    public void FailWhen(Func<ServiceBusMessage, bool> predicate) => _failures.Add(predicate);

    /// <summary>
    /// Holds any send that includes a matching message for <paramref name="duration"/>, as a throttling
    /// broker would. Cancelling the send ends the wait and nothing is recorded: a cut-off send that never landed.
    /// </summary>
    public void StallWhen(Func<ServiceBusMessage, bool> predicate, TimeSpan duration) => _stalls.Add((predicate, duration));

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default) =>
        SendMessagesAsync([message], cancellationToken);

    public override async Task SendMessagesAsync(IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken = default)
    {
        var batch = messages.ToList();
        var stall = _stalls.Where(s => batch.Any(s.Matches)).Select(s => s.For).DefaultIfEmpty().Max();
        if (stall > TimeSpan.Zero)
            await Task.Delay(stall, cancellationToken);

        if (batch.Any(m => _failures.Any(fails => fails(m))))
            throw new ServiceBusException("Broker unavailable (test)", ServiceBusFailureReason.ServiceCommunicationProblem);

        foreach (var message in batch)
            _sent.Enqueue(message);
    }
}
