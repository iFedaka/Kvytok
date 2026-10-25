namespace Kvytok.Domain;

/// <summary>
/// A message the broker is owed. Written in the same transaction as the change it announces,
/// so no committed change exists without a record that its message still has to go out.
/// </summary>
public sealed class OutboxMessage
{
    // Service Bus caps both at 128 characters
    public const int MaxMessageIdLength = 128;
    public const int MaxSubjectLength = 128;

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
    /// <summary>Set by the database.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Null until the broker has accepted it.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
}
