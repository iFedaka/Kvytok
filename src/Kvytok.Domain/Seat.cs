namespace Kvytok.Domain;

public sealed class Seat
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public int Number { get; set; }
    public required SeatStatus Status { get; set; }
}
