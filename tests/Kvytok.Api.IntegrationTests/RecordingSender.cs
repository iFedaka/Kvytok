using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;

namespace Kvytok.Api.IntegrationTests;

/// <summary>Stands in for the reservations queue; keeps every message the API sends.</summary>
public sealed class RecordingSender : ServiceBusSender
{
    private readonly ConcurrentQueue<ServiceBusMessage> _sent = new();

    public IReadOnlyCollection<ServiceBusMessage> Sent => _sent;

    public override string EntityPath => "reservations";

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public override Task SendMessagesAsync(IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken = default)
    {
        foreach (var message in messages)
            _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
