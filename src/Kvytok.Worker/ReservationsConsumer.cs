using Azure.Messaging.ServiceBus;

namespace Kvytok.Worker;

/// <summary>
/// Pumps the reservations queue. A message is completed when its handler returns and abandoned,
/// to be delivered again, when it throws; after the queue's maximum delivery count the broker
/// moves it to the dead-letter queue.
/// </summary>
public sealed class ReservationsConsumer(
    ServiceBusProcessor processor,
    IServiceScopeFactory scopes,
    ILogger<ReservationsConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;
        await processor.StartProcessingAsync(stoppingToken);

        // The processor receives on its own threads; this only waits for shutdown
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        // Lets handlers in flight finish, so their messages are completed rather than redelivered
        await processor.StopProcessingAsync(CancellationToken.None);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ReservationMessageHandler>();

        if (!await handler.HandleAsync(args.Message, args.CancellationToken))
        {
            // Retrying cannot help a message nobody understands
            await args.DeadLetterMessageAsync(
                args.Message,
                "UnknownSubject",
                $"No handler for subject '{args.Message.Subject}'.",
                args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(
            args.Exception,
            "Error in {ErrorSource} on {EntityPath}",
            args.ErrorSource,
            args.EntityPath);
        return Task.CompletedTask;
    }
}
