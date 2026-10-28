using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace Kvytok.Worker;

/// <summary>
/// Pumps the reservations queue. A message is completed when its handler returns. One that fails
/// is sent again with a delay that doubles each time, and goes to the dead-letter queue once it has
/// had <see cref="RetryOptions.MaxAttempts"/>; one that no retry can help goes there at once.
/// </summary>
public sealed class ReservationsConsumer(
    ServiceBusProcessor processor,
    ServiceBusSender queue,
    IServiceScopeFactory scopes,
    IOptions<RetryOptions> retry,
    TimeProvider clock,
    ILogger<ReservationsConsumer> logger) : BackgroundService
{
    /// <summary>
    /// On a retry copy: how many tries came before it. Counted across copies, since each copy is a new
    /// message to the broker and its own delivery count starts again at one.
    /// </summary>
    internal const string PriorAttemptsProperty = "PriorAttempts";

    /// <summary>On a dead-lettered message: how many tries it had, for whoever reads the dead-letter queue.</summary>
    internal const string FailedAttemptsProperty = "FailedAttempts";

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

    internal async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ReservationMessageHandler>();
            await handler.HandleAsync(message, args.CancellationToken);
        }
        catch (UnprocessableMessageException ex)
        {
            logger.LogError(ex, "Message {MessageId} ({Subject}) cannot be processed; dead-lettered as {Reason}",
                message.MessageId, message.Subject, ex.Reason);
            await args.DeadLetterMessageAsync(message, ex.Reason, ex.Message, args.CancellationToken);
        }
        // A shutdown is not the message's fault: let the processor abandon it, and it comes straight back
        catch (Exception ex) when (!args.CancellationToken.IsCancellationRequested)
        {
            await RetryOrDeadLetterAsync(args, ex);
        }
    }

    /// <summary>
    /// Abandoning would bring the message back at once, with no time for whatever failed to recover,
    /// so the retry is a copy scheduled for later and the original is completed. The copy keeps the
    /// message id, and with it the message key: if the copy goes out and the completion fails,
    /// the original comes back as well, and whichever runs second is skipped as a duplicate.
    /// This is also why the queue must not turn on duplicate detection, which would drop the copy.
    /// </summary>
    private async Task RetryOrDeadLetterAsync(ProcessMessageEventArgs args, Exception failure)
    {
        var message = args.Message;
        // A delivery that crashed the worker before it could settle counts too: the broker
        // counts it, and redelivers the same copy with DeliveryCount one higher
        var attempt = PriorAttempts(message) + message.DeliveryCount;
        var options = retry.Value;

        if (attempt >= options.MaxAttempts)
        {
            logger.LogError(failure, "Message {MessageId} ({Subject}) failed {Attempts} times; dead-lettered",
                message.MessageId, message.Subject, attempt);
            // The count starts again, so a message resubmitted from the dead-letter queue once its
            // cause is fixed gets every retry again, not one try and straight back
            await args.DeadLetterMessageAsync(
                message,
                new Dictionary<string, object>
                {
                    [PriorAttemptsProperty] = 0,
                    [FailedAttemptsProperty] = attempt,
                },
                "RetriesExhausted",
                $"{failure.GetType().Name}: {failure.Message}",
                args.CancellationToken);
            return;
        }

        var delay = options.DelayAfter(attempt, Random.Shared);
        var copy = new ServiceBusMessage(message) { ScheduledEnqueueTime = clock.GetUtcNow() + delay };
        copy.ApplicationProperties[PriorAttemptsProperty] = attempt;

        logger.LogWarning(failure,
            "Message {MessageId} ({Subject}) failed on attempt {Attempt} of {MaxAttempts}; trying again in {Delay}",
            message.MessageId, message.Subject, attempt, options.MaxAttempts, delay);

        // Copy first, then complete: a failure between them leaves a duplicate, never a loss
        await queue.SendMessageAsync(copy, args.CancellationToken);
        await args.CompleteMessageAsync(message, args.CancellationToken);
    }

    private static int PriorAttempts(ServiceBusReceivedMessage message) =>
        message.ApplicationProperties.TryGetValue(PriorAttemptsProperty, out var value) && value is int prior
            ? prior
            : 0;

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
