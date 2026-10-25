namespace Kvytok.Domain;

public sealed class Reservation : IHasPendingMessages
{
    private readonly List<PendingMessage> _pending = [];

    public Guid Id { get; set; }
    public Guid SeatId { get; set; }
    public string HolderEmail { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public required ReservationStatus Status { get; set; }
    public byte[] RowVersion { get; set; } = null!;

    /// <summary>Owes <paramref name="body"/> to the broker once this reservation is saved.</summary>
    public void Publish(object body, string messageId, DateTimeOffset? deliverAt = null) =>
        _pending.Add(new PendingMessage(body, messageId, deliverAt));

    IReadOnlyList<PendingMessage> IHasPendingMessages.TakePendingMessages()
    {
        var taken = _pending.ToList();
        _pending.Clear();
        return taken;
    }
}
