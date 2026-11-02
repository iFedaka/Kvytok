namespace Kvytok.Domain;

/// <summary>
/// A message the broker is owed. Written in the same transaction as the change it announces,
/// so no committed change exists without a record that its message still has to go out.
/// One row is one message, never one per change: rows are sent and marked one by one, so a row
/// is either sent or owed, and a change whose messages went out in part retries only the rest.
/// </summary>
public sealed class OutboxMessage
{
    // Service Bus caps both at 128 characters
    public const int MaxMessageIdLength = 128;
    public const int MaxSubjectLength = 128;
    // A W3C traceparent: version, trace id, span id and flags, always this long
    public const int MaxTraceParentLength = 55;

    /// <summary>Identity, so the clustered order is the order the messages were owed in.</summary>
    public long Id { get; set; }
    public string MessageId { get; set; } = null!;
    /// <summary>The contract's type name; the consumer dispatches on it.</summary>
    public string Subject { get; set; } = null!;
    /// <summary>The contract as JSON, ready to send.</summary>
    public string Body { get; set; } = null!;
    /// <summary>
    /// An absolute time, never a delay: a message published late is then simply past due and
    /// delivered at once, rather than due a full delay after whenever the publish happened.
    /// </summary>
    public DateTimeOffset? DeliverAt { get; set; }
    /// <summary>
    /// The span that owed the message, as a W3C traceparent, so whoever sends it continues that
    /// trace. The request's own send runs inside it anyway; the relay runs on a timer and would
    /// otherwise start a trace of its own, cut off from the action that caused the message.
    /// Null when nothing was being traced.
    /// </summary>
    public string? TraceParent { get; set; }
    /// <summary>Set by the database.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Null until the broker has accepted it.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>
    /// When a relay last took the row to send, in database time. A lease rather than a lock: a
    /// relay that dies mid-drain leaves a claim that goes stale, and another relay then takes it.
    /// Null until a relay first takes it; until then <see cref="CreatedAt"/> stands in, being the
    /// moment the request that wrote the row took it to send at once.
    /// </summary>
    public DateTimeOffset? ClaimedAt { get; set; }
}
