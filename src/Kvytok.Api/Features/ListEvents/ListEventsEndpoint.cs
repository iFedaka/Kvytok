using System.Collections.Immutable;
using System.ComponentModel;
using Kvytok.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Kvytok.Api.Features.ListEvents;

// Immutable and sealed, so HybridCache hands every caller the same instance instead of
// deserializing a fresh copy on every hit
[ImmutableObject(true)]
public sealed record ListEventsResponse(ImmutableArray<EventSummary> Events);

[ImmutableObject(true)]
public sealed record EventSummary(Guid EventId, string Name, DateTimeOffset StartsAt, int SeatCount);

/// <summary>
/// The one cached read. Everything in it is fixed when the event is created, so a stale copy is
/// at worst missing an event, never wrong about one. Seat status is not in it on purpose: that
/// changes on every reservation and is read uncached, by <c>GET /events/{id}</c> and by the
/// reservation itself.
/// </summary>
public static class EventListCache
{
    public const string Key = "events:upcoming";

    // The cache is in-process only, so removing the entry on create clears it on this replica
    // alone. Another replica keeps its copy until this runs out: the longest a new event can
    // be missing from the list.
    public static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };
}

public sealed class ListEventsEndpoint : IEndpoint
{
    public const int MaxEvents = 100;

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("/events", Handle)
           .WithName("ListEvents")
           .WithTags("Events")
           .WithSummary($"Lists up to {MaxEvents} upcoming events, soonest first. Cached for up to 30 seconds; " +
                        "seat availability is on GET /events/{eventId}.");

    private static async Task<Ok<ListEventsResponse>> Handle(
        HybridCache cache,
        IServiceScopeFactory scopes,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // Resolved inside the factory, and from a scope of its own: concurrent misses are
        // collapsed into one call, which may outlive the request that started it
        var response = await cache.GetOrCreateAsync(
            EventListCache.Key,
            (scopes, now: clock.GetUtcNow()),
            static async (state, ct) =>
            {
                await using var scope = state.scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<KvytokDbContext>();
                var events = await db.Events
                    .AsNoTracking()
                    .Where(e => e.StartsAt > state.now)
                    .OrderBy(e => e.StartsAt)
                    .ThenBy(e => e.Id)
                    .Take(MaxEvents)
                    .Select(e => new EventSummary(e.Id, e.Name, e.StartsAt, e.SeatCount))
                    .ToListAsync(ct);
                return new ListEventsResponse([.. events]);
            },
            EventListCache.Entry,
            cancellationToken: cancellationToken);

        return TypedResults.Ok(response);
    }
}
