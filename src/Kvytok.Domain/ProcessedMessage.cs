namespace Kvytok.Domain;

/// <summary>
/// A message the worker has already acted on, keyed by its broker message id. Written in the same
/// transaction as what the message decided, so a redelivery finds it and is recognised as the same
/// message rather than taken for a new one: two deliveries of one late payment are one refund, not two.
/// </summary>
public sealed class ProcessedMessage
{
    public string MessageId { get; set; } = null!;
    /// <summary>Set by the database.</summary>
    public DateTimeOffset ProcessedAt { get; set; }
}
