namespace Kvytok.Worker;

/// <summary>
/// A message that fails the same way however often it is tried: a subject nobody handles, a body
/// that does not read as its contract. It goes straight to the dead-letter queue instead of
/// spending its retries.
/// </summary>
public sealed class UnprocessableMessageException(string reason, string description, Exception? inner = null)
    : Exception(description, inner)
{
    /// <summary>Short and stable, for the dead-letter reason.</summary>
    public string Reason { get; } = reason;
}
