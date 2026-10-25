using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Kvytok.Contracts;
using Kvytok.Domain;

namespace Kvytok.Worker;

/// <summary>
/// Turns a message from the reservations queue into a lifecycle step. Every step is safe to run
/// twice, since the broker delivers at least once: a repeat finds the reservation already decided
/// and changes nothing.
/// </summary>
public sealed class ReservationMessageHandler(
    ReservationLifecycle lifecycle,
    ILogger<ReservationMessageHandler> logger)
{
    /// <returns>false if the message is of a kind this worker does not know.</returns>
    public async Task<bool> HandleAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        switch (message.Subject)
        {
            case nameof(PaymentReceived):
                await ConfirmAsync(Read<PaymentReceived>(message), cancellationToken);
                return true;
            case nameof(ReservationExpiryDue):
                await ExpireAsync(Read<ReservationExpiryDue>(message), cancellationToken);
                return true;
            case nameof(ReservationCreated):
                // Nothing to do yet: the expiry was scheduled in the same batch
                return true;
            default:
                return false;
        }
    }

    private async Task ConfirmAsync(PaymentReceived payment, CancellationToken cancellationToken)
    {
        var before = await lifecycle.ConfirmAsync(payment.ReservationId, cancellationToken);
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
                    payment.ReservationId, before);
                break;
        }
    }

    private async Task ExpireAsync(ReservationExpiryDue due, CancellationToken cancellationToken)
    {
        var before = await lifecycle.ExpireAsync(due.ReservationId, cancellationToken);
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

    private static T Read<T>(ServiceBusReceivedMessage message) =>
        message.Body.ToObjectFromJson<T>(JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException($"Message {message.MessageId} has an empty {typeof(T).Name} body.");
}
