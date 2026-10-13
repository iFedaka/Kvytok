using System.Net;
using System.Net.Http.Json;
using Kvytok.Api.Features.GetReservation;
using Kvytok.Api.Features.ReserveSeat;
using Kvytok.Domain;

namespace Kvytok.Api.Tests.Features;

public sealed class GetReservationTests(ApiFixture api)
{
    [Fact]
    public async Task Returns_The_Reservation_With_Its_Event_And_Seat()
    {
        var eventId = await api.CreateEventAsync();
        var reserve = await api.Client.PostAsJsonAsync($"/events/{eventId}/reservations",
            new ReserveSeatRequest(4, "holder@example.com"), TestContext.Current.CancellationToken);
        var created = await reserve.Content.ReadFromJsonAsync<ReserveSeatResponse>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);

        var reservation = await api.Client.GetFromJsonAsync<GetReservationResponse>(
            reserve.Headers.Location,
            ApiFixture.Json,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new GetReservationResponse(created!.ReservationId, eventId, 4, ReservationStatus.Held, created.ExpiresAt),
            reservation);
    }

    [Fact]
    public async Task Does_Not_Expose_The_Holder_Email()
    {
        var eventId = await api.CreateEventAsync();
        var reserve = await api.Client.PostAsJsonAsync($"/events/{eventId}/reservations",
            new ReserveSeatRequest(1, "private@example.com"), TestContext.Current.CancellationToken);

        var json = await api.Client.GetStringAsync(reserve.Headers.Location, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("private@example.com", json);
    }

    [Fact]
    public async Task Returns_404_For_An_Unknown_Reservation()
    {
        var response = await api.Client.GetAsync($"/reservations/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
