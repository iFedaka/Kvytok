using System.Net;
using System.Net.Http.Json;
using Kvytok.Api.Features.CreateEvent;
using Kvytok.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Api.Tests.Features;

public sealed class CreateEventTests(ApiFixture api)
{
    [Fact]
    public async Task Creates_The_Event_And_Its_Seats_Numbered_From_One_All_Free()
    {
        var name = $"Test {Guid.NewGuid():N}";
        var startsAt = api.Clock.GetUtcNow().AddDays(30);

        var response = await api.Client.PostAsJsonAsync("/events", new CreateEventRequest(name, startsAt, 200),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateEventResponse>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal($"/events/{body!.EventId}", response.Headers.Location?.OriginalString);

        var ev = await api.WithDbAsync(db => db.Events.SingleAsync(e => e.Id == body.EventId));
        Assert.Equal(name, ev.Name);
        Assert.Equal(startsAt, ev.StartsAt);
        Assert.Equal(200, ev.SeatCount);

        var seats = await api.WithDbAsync(db => db.Seats
            .Where(s => s.EventId == body.EventId)
            .OrderBy(s => s.Number)
            .ToListAsync());
        Assert.Equal(Enumerable.Range(1, 200), seats.Select(s => s.Number));
        Assert.All(seats, s => Assert.Equal(SeatStatus.Free, s.Status));
    }

    [Fact]
    public async Task Trims_The_Name()
    {
        var name = $"Test {Guid.NewGuid():N}";

        var response = await api.Client.PostAsJsonAsync("/events",
            new CreateEventRequest($"  {name}  ", api.Clock.GetUtcNow().AddDays(1), 1),
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<CreateEventResponse>(ApiFixture.Json,
            TestContext.Current.CancellationToken);
        var ev = await api.WithDbAsync(db => db.Events.SingleAsync(e => e.Id == body!.EventId));
        Assert.Equal(name, ev.Name);
    }

    public static TheoryData<string?, int, int, string> InvalidRequests => new()
    {
        // name, days from now, seat count, field expected to fail
        { "", 1, 10, "Name" },
        { "   ", 1, 10, "Name" },
        { null, 1, 10, "Name" },
        { new string('x', 201), 1, 10, "Name" },
        { "Concert", 0, 10, "StartsAt" },
        { "Concert", -1, 10, "StartsAt" },
        { "Concert", 1, 0, "SeatCount" },
        { "Concert", 1, -5, "SeatCount" },
        { "Concert", 1, 10_001, "SeatCount" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Rejects_Invalid_Input_With_A_Validation_Problem(
        string? name, int daysFromNow, int seatCount, string field)
    {
        var response = await api.Client.PostAsJsonAsync("/events",
            new CreateEventRequest(name!, api.Clock.GetUtcNow().AddDays(daysFromNow), seatCount),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal([field], problem!.Errors.Keys);
    }

    [Fact]
    public async Task Reports_Every_Invalid_Field_At_Once_And_Writes_Nothing()
    {
        var response = await api.Client.PostAsJsonAsync("/events",
            new CreateEventRequest("", api.Clock.GetUtcNow().AddDays(-1), 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equivalent(new[] { "Name", "StartsAt", "SeatCount" }, problem!.Errors.Keys);

        // other tests create events in parallel, so a row count proves nothing;
        // no test ever creates an event with an empty name, so look for that
        Assert.False(await api.WithDbAsync(db => db.Events.AnyAsync(e => e.Name == "")));
    }
}
