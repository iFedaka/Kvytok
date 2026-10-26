namespace Kvytok.Api.Infrastructure;

/// <summary>Bound from the "Outbox" section.</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>The longest the broker gets to accept a send, or the outbox to record that it did.</summary>
    public static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Held back from the end of a claim for recording what was sent: one UPDATE, normally milliseconds.</summary>
    public static readonly TimeSpan MarkReserve = TimeSpan.FromSeconds(1);

    /// <summary>
    /// One number doing two jobs, so tuning it tunes both. It is the grace a fresh row gets while
    /// its own request sends it, and the lease a relay holds on a claimed batch. Neither sender
    /// acts past it (see <see cref="OwnedFor"/>), so shorter means a lost publish is found sooner
    /// and a slow broker has less time per send; it never means two senders at once.
    /// It must stay well under <c>Reservation:HoldFor</c>: a lost publish waits this long to be
    /// claimable, up to one drain interval for a drain, and one <see cref="SendTimeout"/> to go out,
    /// and an expiry that goes out after the hold ends releases the seat late.
    /// </summary>
    public TimeSpan Lease { get; set; }

    /// <summary>
    /// How long a sender may act on rows it holds, sending and then recording that it sent: never
    /// past the lease, since a claim must outlive the operation it protects, and that operation
    /// ends when the send is written down, not when the broker accepts it.
    /// </summary>
    public TimeSpan OwnedFor => Lease < PublishTimeout ? Lease : PublishTimeout;

    /// <summary>
    /// How long sending may run: <see cref="OwnedFor"/> less what is kept for recording it. A send
    /// still running at the end would leave its record to land after the claim lapsed; cut off
    /// instead, the row is simply sent by whoever holds it next.
    /// </summary>
    public TimeSpan SendTimeout => OwnedFor - MarkReserve;
}
