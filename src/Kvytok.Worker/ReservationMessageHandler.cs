using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Kvytok.Contracts;
using Kvytok.Domain;

namespace Kvytok.Worker;

/// <summary>
/// Turns a message from the reservations queue into a lifecycle step. The broker delivers at least
/// once, so a message can arrive again: the step is keyed by the message id and a repeat is skipped,
/// and a different message for a reservation that is already decided changes nothing.
/// </summary>
public sealed class ReservationMessageHandler(
    ReservationLifecycle lifecycle,
    ILogger<ReservationMessageHandler> logger)
{
    /// <exception cref="UnprocessableMessageException">No retry can help this message.</exception>
    public async Task HandleAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        switch (message.Subject)
        {
            case nameof(PaymentReceived):
                await ConfirmAsync(message, Read<PaymentReceived>(message), cancellationToken);
                break;
            case nameof(ReservationExpiryDue):
                await ExpireAsync(message, Read<ReservationExpiryDue>(message), cancellationToken);
                break;
            case nameof(ReservationCreated):
                // Nothing to process. It is sent the moment a seat is held, so a worker scaled to zero
                // is already running when the payment arrives (docs/deploy.md, "When the app looks
                // dead"); the expiry is scheduled and wakes the worker on its own. With a minimum
                // of one replica this would have no job left, and could go.
                break;
            default:
                throw new UnprocessableMessageException(
                    "UnknownSubject", $"No handler for subject '{message.Subject}'.");
        }
    }

    private async Task ConfirmAsync(
        ServiceBusReceivedMessage message, PaymentReceived payment, CancellationToken cancellationToken)
    {
        var step = await lifecycle.ConfirmAsync(payment.ReservationId, message.MessageId, cancellationToken);
        if (step.IsAlreadyHandled)
        {
            logger.LogInformation("Payment {PaymentReference} for reservation {ReservationId} was delivered again; skipped",
                payment.PaymentReference, payment.ReservationId);
            return;
        }

        var before = step.Before;
        switch (before)
        {
            case ReservationStatus.Held:
                logger.LogInformation(
                    "Reservation {ReservationId} confirmed by payment {PaymentReference}",
                    payment.ReservationId,
                    payment.PaymentReference);
                break;
            case ReservationStatus.Confirmed:
                logger.LogInformation(
                    "Payment {PaymentReference} for reservation {ReservationId} changed nothing: already confirmed",
                    payment.PaymentReference,
                    payment.ReservationId);
                break;
            case null:
                logger.LogWarning(
                    "Payment {PaymentReference} names reservation {ReservationId}, which does not exist",
                    payment.PaymentReference,
                    payment.ReservationId);
                break;
            default:
                // Too late: the seat may already belong to someone else. A real system would refund here.
                logger.LogWarning(
                    "Payment {PaymentReference} arrived after reservation {ReservationId} was {Status}; not confirmed",
                    payment.PaymentReference,
                    payment.ReservationId,
                    before);
                break;
        }
    }

    private async Task ExpireAsync(
        ServiceBusReceivedMessage message, ReservationExpiryDue due, CancellationToken cancellationToken)
    {
        var step = await lifecycle.ExpireAsync(due.ReservationId, message.MessageId, cancellationToken);
        if (step.IsAlreadyHandled)
        {
            logger.LogInformation("Expiry for reservation {ReservationId} was delivered again; skipped", due.ReservationId);
            return;
        }

        var before = step.Before;
        switch (before)
        {
            case ReservationStatus.Held:
                logger.LogInformation("Reservation {ReservationId} expired unpaid; its seat is free again", due.ReservationId);
                break;
            case null:
                logger.LogWarning("Expiry is due for reservation {ReservationId}, which does not exist", due.ReservationId);
                break;
            default:
                // The usual case for a paid reservation, not a missed one
                logger.LogInformation("Expiry for reservation {ReservationId} changed nothing: already {Status}",
                    due.ReservationId, before);
                break;
        }
    }

    private static T Read<T>(ServiceBusReceivedMessage message)
    {
        T? body;
        try
        {
            body = message.Body.ToObjectFromJson<T>(JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            throw new UnprocessableMessageException(
                "MalformedBody", $"Message {message.MessageId} is not a {typeof(T).Name}: {ex.Message}", ex);
        }

        return body ?? throw new UnprocessableMessageException(
            "MalformedBody", $"Message {message.MessageId} has an empty {typeof(T).Name} body.");
    }
}
