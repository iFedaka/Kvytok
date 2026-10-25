using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kvytok.Domain;

/// <summary>
/// Turns the messages tracked entities owe into <see cref="OutboxMessage"/> rows just before a save.
/// The rows join the same SaveChanges, and one SaveChanges is one transaction, so a change and
/// the record of its message commit together or not at all. Two separate saves would give no
/// such guarantee; doing it here means a caller cannot split them by accident.
/// Added through <see cref="KvytokDbContextOptions.AddKvytokInterceptors"/>; the context refuses to start without it.
/// </summary>
public sealed class OutboxInterceptor : SaveChangesInterceptor
{
    public static OutboxInterceptor Instance { get; } = new();

    private OutboxInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context)
            WriteOutbox(context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
            WriteOutbox(context);
        return ValueTask.FromResult(result);
    }

    private static void WriteOutbox(DbContext context)
    {
        // materialised first: adding rows while enumerating the tracker would invalidate it
        var pending = context.ChangeTracker.Entries<IHasPendingMessages>()
            .SelectMany(e => e.Entity.TakePendingMessages())
            .ToList();

        foreach (var message in pending)
        {
            context.Add(new OutboxMessage
            {
                MessageId = message.MessageId,
                Subject = message.Body.GetType().Name,
                Body = JsonSerializer.Serialize(message.Body, message.Body.GetType(), JsonSerializerOptions.Web),
                DeliverAt = message.DeliverAt,
            });
        }
    }
}
