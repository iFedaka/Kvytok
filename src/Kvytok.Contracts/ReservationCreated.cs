namespace Kvytok.Contracts;

/// <summary>
/// Published to the reservations queue once a seat hold is committed.
/// The message id is the reservation id, so a resend can be recognised as a duplicate.
/// </summary>
public sealed record ReservationCreated(
    Guid ReservationId,
    Guid EventId,
    Guid SeatId,
    int SeatNumber,
    DateTimeOffset ExpiresAt);
