namespace Kvytok.Domain;

public sealed class Reservation
{
    public Guid Id { get; set; }
    public Guid SeatId { get; set; }
    public string HolderEmail { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public required ReservationStatus Status { get; set; }
    public byte[] RowVersion { get; set; } = null!;
}