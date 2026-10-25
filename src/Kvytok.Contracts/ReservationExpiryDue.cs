namespace Kvytok.Contracts;

/// <summary>
/// Scheduled alongside <see cref="ReservationCreated"/> for the moment the hold runs out; the broker
/// holds it until then. By the time it arrives the reservation may already be confirmed, and then
/// it changes nothing.
/// </summary>
public sealed record ReservationExpiryDue(Guid ReservationId);
