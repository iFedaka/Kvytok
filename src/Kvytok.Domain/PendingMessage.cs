namespace Kvytok.Domain;

/// <summary>A message an entity owes once its change commits; see <see cref="IHasPendingMessages"/>.</summary>
/// <param name="Body">The contract, serialized as JSON.</param>
/// <param name="MessageId">Stable across resends, so the broker can recognise a duplicate.</param>
/// <param name="DeliverAt">When the broker should make it visible; absolute, see <see cref="OutboxMessage.DeliverAt"/>.</param>
public sealed record PendingMessage(object Body, string MessageId, DateTimeOffset? DeliverAt = null);

/// <summary>
/// An entity that carries the messages its change owes. <see cref="OutboxInterceptor"/> moves them
/// into the outbox inside the same SaveChanges as the entity, so they cannot be saved apart from it.
/// </summary>
public interface IHasPendingMessages
{
    /// <summary>Returns the pending messages and forgets them, so a second save does not write them again.</summary>
    IReadOnlyList<PendingMessage> TakePendingMessages();
}
