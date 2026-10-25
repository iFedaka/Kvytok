namespace Kvytok.Contracts;

/// <summary>
/// Published by the payment webhook; the worker decides whether it confirms the reservation.
/// The message id is derived from the payment reference, so a provider retrying the webhook
/// produces a message the broker can recognise as a duplicate.
/// </summary>
public sealed record PaymentReceived(
    Guid ReservationId,
    string PaymentReference);
