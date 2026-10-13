using System.Net;
using System.Net.Http.Json;
using Kvytok.Api.Features.GetEvent;
using Kvytok.Api.Features.ReserveSeat;
using Kvytok.Domain;

namespace Kvytok.Api.Tests.Features;

public sealed class GetEventTests(ApiFixture api)
{
    [Fact]
    public async Task Returns_The_Event_With_Every_Seat_Free()
    {
        var eventId = await api.CreateEventAsync(seatCount: 5);

        var ev = await api.Client.GetFromJsonAsync<GetEventResponse>(
            $"/events/{eventId}",
            ApiFixture.Json,
            TestContext.Current.CancellationToken);

        Assert.Equal(eventId, ev!.EventId);
        Assert.Equal(5, ev.SeatCount);
        Assert.Equal(5, ev.FreeSeatCount);
        Assert.Equal([1, 2, 3, 4, 5], ev.Seats.Select(s => s.Number));
        Assert.All(ev.Seats, s => Assert.Equal(SeatStatus.Free, s.Status));
    }

    [Fact]
    public async Task Shows_A_Reserved_Seat_As_Held()
    {
        var eventId = await api.CreateEventAsync(seatCount: 5);
        var reserve = await api.Client.PostAsJsonAsync(
            $"/events/{eventId}/reservations",
            new ReserveSeatRequest(3, "holder@example.com"),
            TestContext.Current.CancellationToken);
        reserve.EnsureSuccessStatusCode();

        var ev = await api.Client.GetFromJsonAsync<GetEventResponse>(
            $"/events/{eventId}",
            ApiFixture.Json,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, ev!.FreeSeatCount);
        Assert.Equal(SeatStatus.Held, ev.Seats.Single(s => s.Number == 3).Status);
    }

    [Fact]
    public async Task Writes_Seat_Status_As_A_Name()
    {
        var eventId = await api.CreateEventAsync(seatCount: 1);

        var json = await api.Client.GetStringAsync($"/events/{eventId}", TestContext.Current.CancellationToken);

        Assert.Contains("\"status\":\"Free\"", json);
    }

    [Fact]
    public async Task Returns_404_For_An_Unknown_Event()
    {
        var response = await api.Client.GetAsync($"/events/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
