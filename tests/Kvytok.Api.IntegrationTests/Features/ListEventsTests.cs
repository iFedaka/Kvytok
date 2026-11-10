using System.Net.Http.Json;
using Kvytok.Api.Features.CreateEvent;
using Kvytok.Api.Features.ListEvents;
using Kvytok.Domain;

namespace Kvytok.Api.IntegrationTests.Features;

public sealed class ListEventsTests(ApiFixture api)
{
    [Fact]
    public async Task Lists_An_Event_As_Soon_As_It_Is_Created()
    {
        await ListAsync(); // a cached list from before the event exists
        var eventId = await CreateSoonAsync(seatCount: 3);

        var listed = Assert.Single((await ListAsync()).Events, e => e.EventId == eventId);

        Assert.Equal(3, listed.SeatCount);
    }

    [Fact]
    public async Task Lists_Only_Upcoming_Events_Soonest_First()
    {
        var past = await InsertDirectlyAsync(api, api.Clock.GetUtcNow().AddDays(-1));
        // created through the Api, so the list is rebuilt with the row above already in the table
        var later = await CreateSoonAsync(startsIn: TimeSpan.FromMinutes(20));
        var sooner = await CreateSoonAsync(startsIn: TimeSpan.FromMinutes(10));

        var events = (await ListAsync()).Events;

        Assert.DoesNotContain(events, e => e.EventId == past);
        Assert.True(events.IndexOf(events.Single(e => e.EventId == sooner))
                    < events.IndexOf(events.Single(e => e.EventId == later)));
        Assert.Equal(events.OrderBy(e => e.StartsAt).Select(e => e.EventId), events.Select(e => e.EventId));
    }

    private async Task<Guid> CreateSoonAsync(int seatCount = 1, TimeSpan? startsIn = null)
    {
        // far sooner than other tests' events, which start in 30 days, so it is inside the first page
        var response = await api.Client.PostAsJsonAsync("/events",
            new CreateEventRequest($"Soon {Guid.NewGuid():N}", api.Clock.GetUtcNow() + (startsIn ?? TimeSpan.FromMinutes(5)), seatCount),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateEventResponse>(ApiFixture.Json, TestContext.Current.CancellationToken))!.EventId;
    }

    private Task<ListEventsResponse> ListAsync() => ListAsync(api);

    internal static async Task<ListEventsResponse> ListAsync(ApiFixture api) =>
        (await api.Client.GetFromJsonAsync<ListEventsResponse>("/events", ApiFixture.Json, TestContext.Current.CancellationToken))!;

    /// <summary>Written past the Api, as another replica's create looks to this one: nothing clears the cache.</summary>
    internal static Task<Guid> InsertDirectlyAsync(ApiFixture api, DateTimeOffset startsAt) =>
        api.WithDbAsync(async db =>
        {
            var ev = new Event { Id = Guid.CreateVersion7(), Name = $"Direct {Guid.NewGuid():N}", StartsAt = startsAt, SeatCount = 1 };
            db.Events.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return ev.Id;
        });
}

// Never runs alongside another test, because a create elsewhere would clear the cache between
// this test's two reads. That is all DisableParallelization promises: not when the collection runs,
// first, last or in between. The test needs no particular order, only no company.
[CollectionDefinition(DisableParallelization = true)]
public sealed class CacheSensitive;

[Collection(typeof(CacheSensitive))]
public sealed class ListEventsCacheTests(ApiFixture api)
{
    // Covers the stale read, not the expiry that ends it. The in-memory layer expires entries on
    // the wall clock: the fixture's FakeTimeProvider does not reach it (checked by hand: advancing
    // the fake leaves an entry cached, two real seconds expire a one-second one). So this test holds
    // while its two reads are under 30 real seconds apart, and seeing expiry would take a 30-second wait.
    [Fact]
    public async Task Serves_Its_Own_Copy_When_An_Event_Is_Created_Past_This_Replica()
    {
        await ListEventsTests.ListAsync(api);
        var elsewhere = await ListEventsTests.InsertDirectlyAsync(api, api.Clock.GetUtcNow().AddMinutes(1));

        var events = (await ListEventsTests.ListAsync(api)).Events;

        Assert.DoesNotContain(events, e => e.EventId == elsewhere);
    }
}
