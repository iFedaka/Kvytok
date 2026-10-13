namespace Kvytok.Domain;

public sealed class Event
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset StartsAt { get; set; }
    public int SeatCount { get; set; }
}