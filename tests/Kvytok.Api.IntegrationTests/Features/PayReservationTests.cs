using System.Net;
using System.Net.Http.Json;
using Azure.Messaging.ServiceBus;
using Kvytok.Api.Features.PayReservation;
using Kvytok.Contracts;
using Kvytok.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Api.IntegrationTests.Features;

public sealed class PayReservationTests(ApiFixture api)
{
    [Fact]
    public async Task Accepts_A_Payment_And_Publishes_It_For_The_Worker()
    {
        var reservationId = await api.ReserveAsync(await api.CreateEventAsync());

        var response = await PayAsync(reservationId, "pay_123");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/reservations/{reservationId}", response.Headers.Location?.OriginalString);
        var message = Assert.Single(SentFor(reservationId));
        Assert.Equal($"{reservationId}:payment:pay_123", message.MessageId);
        Assert.Equal("application/json", message.ContentType);
        Assert.Equal(new PaymentReceived(reservationId, "pay_123"),
            message.Body.ToObjectFromJson<PaymentReceived>(ApiFixture.Json));
    }

    [Fact]
    public async Task Leaves_The_Reservation_Held_Because_Confirming_Is_The_Workers_Decision()
    {
        var reservationId = await api.ReserveAsync(await api.CreateEventAsync());

        (await PayAsync(reservationId, "pay_123")).EnsureSuccessStatusCode();

        var reservation = await api.WithDbAsync(db => db.Reservations.SingleAsync(r => r.Id == reservationId));
        Assert.Equal(ReservationStatus.Held, reservation.Status);
    }

    [Fact]
    public async Task The_Same_Payment_Reported_Twice_Carries_The_Same_Message_Id()
    {
        var reservationId = await api.ReserveAsync(await api.CreateEventAsync());

        (await PayAsync(reservationId, "pay_123")).EnsureSuccessStatusCode();
        (await PayAsync(reservationId, "pay_123")).EnsureSuccessStatusCode();

        var ids = SentFor(reservationId).Select(m => m.MessageId).Distinct();
        Assert.Single(ids);
    }

    [Fact]
    public async Task Returns_404_For_An_Unknown_Reservation()
    {
        var reservationId = Guid.NewGuid();

        var response = await PayAsync(reservationId, "pay_123");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(SentFor(reservationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Rejects_A_Missing_Payment_Reference(string? reference)
    {
        var reservationId = await api.ReserveAsync(await api.CreateEventAsync());

        var response = await PayAsync(reservationId, reference!);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal(["PaymentReference"], problem!.Errors.Keys);
    }

    // the sender is shared by the whole run, so pick out this test's reservation
    private IEnumerable<ServiceBusMessage> SentFor(Guid reservationId) =>
        api.ReservationsQueue.Sent.Where(m =>
            m.Subject == nameof(PaymentReceived)
            && m.Body.ToObjectFromJson<PaymentReceived>(ApiFixture.Json)!.ReservationId == reservationId);

    private Task<HttpResponseMessage> PayAsync(Guid reservationId, string reference) =>
        api.Client.PostAsJsonAsync($"/reservations/{reservationId}/payments",
            new PayReservationRequest(reference), TestContext.Current.CancellationToken);
}
