using System.Net;
using System.Net.Http.Json;
using Azure.Messaging.ServiceBus;
using Kvytok.Api.Features.ReserveSeat;
using Kvytok.Api.IntegrationTests.Infrastructure;
using Kvytok.Domain;
using Kvytok.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Api.IntegrationTests.Features;

// it drains the outbox in one test; see OutboxRelayTests
[Collection(OutboxDrains.Collection)]
public sealed class ReserveSeatTests(ApiFixture api)
{
    [Fact]
    public async Task Holds_A_Free_Seat_For_Ten_Minutes()
    {
        var eventId = await api.CreateEventAsync();

        var response = await ReserveAsync(eventId, 7, "  holder@example.com ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReserveSeatResponse>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal($"/reservations/{body!.ReservationId}", response.Headers.Location?.OriginalString);
        Assert.Equal(api.Clock.GetUtcNow().AddMinutes(10), body.ExpiresAt);

        var seat = await api.WithDbAsync(db => db.Seats.SingleAsync(s => s.EventId == eventId && s.Number == 7));
        Assert.Equal(SeatStatus.Held, seat.Status);

        var reservation = await api.WithDbAsync(db => db.Reservations.SingleAsync(r => r.Id == body.ReservationId));
        Assert.Equal(seat.Id, reservation.SeatId);
        Assert.Equal(ReservationStatus.Held, reservation.Status);
        Assert.Equal("holder@example.com", reservation.HolderEmail);
    }

    [Fact]
    public async Task Publishes_ReservationCreated_After_The_Hold_Is_Committed()
    {
        var eventId = await api.CreateEventAsync();

        var response = await ReserveAsync(eventId, 7, "holder@example.com");

        var body = await response.Content.ReadFromJsonAsync<ReserveSeatResponse>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        var message = Assert.Single(SentFor(eventId));
        Assert.Equal(body!.ReservationId.ToString(), message.MessageId);
        Assert.Equal(nameof(ReservationCreated), message.Subject);
        Assert.Equal("application/json", message.ContentType);

        var seatId = await api.WithDbAsync(db => db.Seats
            .Where(s => s.EventId == eventId && s.Number == 7)
            .Select(s => s.Id)
            .SingleAsync());
        Assert.Equal(
            new ReservationCreated(body.ReservationId, eventId, seatId, 7, body.ExpiresAt),
            message.Body.ToObjectFromJson<ReservationCreated>(ApiFixture.Json));
    }

    [Fact]
    public async Task Schedules_The_Expiry_For_The_Moment_The_Hold_Runs_Out()
    {
        var eventId = await api.CreateEventAsync();

        var response = await ReserveAsync(eventId, 7, "holder@example.com");

        var body = await response.Content.ReadFromJsonAsync<ReserveSeatResponse>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        var message = Assert.Single(api.ReservationsQueue.Sent, m =>
            m.Subject == nameof(ReservationExpiryDue)
            && m.Body.ToObjectFromJson<ReservationExpiryDue>(ApiFixture.Json)!.ReservationId == body!.ReservationId);
        Assert.Equal($"{body!.ReservationId}:expiry", message.MessageId);
        Assert.Equal(body.ExpiresAt, message.ScheduledEnqueueTime);
    }

    [Fact]
    public async Task Writes_Both_Messages_To_The_Outbox_And_Marks_Them_Published()
    {
        var eventId = await api.CreateEventAsync();

        var response = await ReserveAsync(eventId, 7, "holder@example.com");

        var body = await response.Content.ReadFromJsonAsync<ReserveSeatResponse>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        var rows = await OutboxFor(body!.ReservationId);
        Assert.Equal(
            [body.ReservationId.ToString(), $"{body.ReservationId}:expiry"],
            rows.Select(r => r.MessageId));
        Assert.All(rows, r => Assert.Equal(api.Clock.GetUtcNow(), r.PublishedAt));

        var created = rows.Single(r => r.Subject == nameof(ReservationCreated));
        Assert.Null(created.DeliverAt);
        var expiry = rows.Single(r => r.Subject == nameof(ReservationExpiryDue));
        // the hold's own end, not a delay counted from whenever the message is sent
        Assert.Equal(body.ExpiresAt, expiry.DeliverAt);
    }

    [Fact]
    public async Task Keeps_The_Hold_And_Leaves_The_Messages_Owed_When_The_Publish_Fails()
    {
        var eventId = await api.CreateEventAsync();
        api.ReservationsQueue.FailWhen(m =>
            m.Subject == nameof(ReservationCreated)
            && m.Body.ToObjectFromJson<ReservationCreated>(ApiFixture.Json)!.EventId == eventId);

        var response = await ReserveAsync(eventId, 7, "holder@example.com");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReserveSeatResponse>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Empty(SentFor(eventId));
        // the batch is atomic: the expiry was not sent without its event either
        Assert.DoesNotContain(api.ReservationsQueue.Sent, m => m.MessageId == $"{body!.ReservationId}:expiry");

        var rows = await OutboxFor(body!.ReservationId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.PublishedAt));
    }

    [Fact]
    public async Task The_Next_Drain_Publishes_What_The_Immediate_Publish_Could_Not()
    {
        var eventId = await api.CreateEventAsync();
        var brokerDown = true;
        api.ReservationsQueue.FailWhen(m =>
            brokerDown
            && m.Subject == nameof(ReservationCreated)
            && m.Body.ToObjectFromJson<ReservationCreated>(ApiFixture.Json)!.EventId == eventId);
        var response = await ReserveAsync(eventId, 7, "holder@example.com");
        var body = await response.Content.ReadFromJsonAsync<ReserveSeatResponse>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Empty(SentFor(eventId));
        brokerDown = false;
        // past the lease, as a request that died after its commit would leave them
        await api.WithDbAsync(db => db.OutboxMessages
            .Where(m => m.MessageId.StartsWith(body!.ReservationId.ToString()))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, m => m.CreatedAt.AddMinutes(-1))));

        await api.Relay.DrainAsync(TestContext.Current.CancellationToken);

        var created = Assert.Single(SentFor(eventId));
        Assert.Equal(body!.ReservationId.ToString(), created.MessageId);
        var expiry = Assert.Single(api.ReservationsQueue.Sent, m => m.MessageId == $"{body.ReservationId}:expiry");
        // still due when the hold ends, not a hold's length after the late publish
        Assert.Equal(body.ExpiresAt, expiry.ScheduledEnqueueTime);
        var rows = await OutboxFor(body.ReservationId);
        Assert.All(rows, r => Assert.Equal(api.Clock.GetUtcNow(), r.PublishedAt));
    }

    [Fact]
    public async Task Publishes_Nothing_When_The_Seat_Is_Taken()
    {
        var eventId = await api.CreateEventAsync();
        (await ReserveAsync(eventId, 7, "first@example.com")).EnsureSuccessStatusCode();
        var publishedByFirst = SentFor(eventId).Count();

        var response = await ReserveAsync(eventId, 7, "second@example.com");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(publishedByFirst, SentFor(eventId).Count());
        // only the first reservation's two rows exist
        Assert.Equal(2, await OutboxCountFor(eventId));
    }

    [Fact]
    public async Task Returns_409_When_The_Seat_Is_Already_Held()
    {
        var eventId = await api.CreateEventAsync();
        (await ReserveAsync(eventId, 7, "first@example.com")).EnsureSuccessStatusCode();

        var response = await ReserveAsync(eventId, 7, "second@example.com");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal(409, problem!.Status);
        Assert.Contains("Seat 7", problem.Detail);
    }

    [Theory]
    [InlineData(SeatStatus.Held)]
    [InlineData(SeatStatus.Booked)]
    public async Task Returns_409_For_A_Seat_That_Is_Not_Free_Even_Without_An_Active_Reservation(SeatStatus status)
    {
        // The unique index on Reservations.SeatId cannot help here, so this
        // isolates the conditional update that guards the seat row.
        var eventId = await api.CreateEventAsync();
        await api.WithDbAsync(db => db.Seats
            .Where(s => s.EventId == eventId && s.Number == 7)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status)));

        var response = await ReserveAsync(eventId, 7, "holder@example.com");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Exactly_One_Of_Many_Concurrent_Requests_For_One_Seat_Wins()
    {
        const int contenders = 200;
        var eventId = await api.CreateEventAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, contenders)
            .Select(i => ReserveAsync(eventId, 7, $"user{i}@example.com")));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(contenders - 1, statuses.Count(s => s == HttpStatusCode.Conflict));

        var reservations = await api.WithDbAsync(db => db.Reservations
            .CountAsync(r => db.Seats.Any(s => s.Id == r.SeatId && s.EventId == eventId)));
        Assert.Equal(1, reservations);
        Assert.Single(SentFor(eventId));
        Assert.Equal(2, await OutboxCountFor(eventId));
    }

    [Fact]
    public async Task Concurrent_Requests_For_Different_Seats_All_Succeed()
    {
        var eventId = await api.CreateEventAsync(seatCount: 50);

        var responses = await Task.WhenAll(
            Enumerable.Range(1, 50)
                .Select(n => ReserveAsync(eventId, n, $"user{n}@example.com")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var free = await api.WithDbAsync(db => db.Seats
            .CountAsync(s => s.EventId == eventId && s.Status == SeatStatus.Free));
        Assert.Equal(0, free);
    }

    [Fact]
    public async Task Returns_404_For_An_Unknown_Event()
    {
        var response = await ReserveAsync(Guid.NewGuid(), 1, "holder@example.com");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Returns_404_For_A_Seat_Number_Beyond_The_Event()
    {
        var eventId = await api.CreateEventAsync(seatCount: 10);

        var response = await ReserveAsync(eventId, 11, "holder@example.com");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(0, "holder@example.com", "SeatNumber")]
    [InlineData(-1, "holder@example.com", "SeatNumber")]
    [InlineData(1, "", "HolderEmail")]
    [InlineData(1, "   ", "HolderEmail")]
    [InlineData(1, null, "HolderEmail")]
    [InlineData(1, "not-an-email", "HolderEmail")]
    public async Task Rejects_Invalid_Input_With_A_Validation_Problem(int seatNumber, string? email, string field)
    {
        var eventId = await api.CreateEventAsync();

        var response = await ReserveAsync(eventId, seatNumber, email!);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal([field], problem!.Errors.Keys);
    }

    [Fact]
    public async Task Rejects_An_Email_Longer_Than_254_Characters()
    {
        var eventId = await api.CreateEventAsync();

        var response = await ReserveAsync(eventId, 1, new string('a', 243) + "@example.com");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // the sender is shared by the whole run, so pick out this test's event
    private IEnumerable<ServiceBusMessage> SentFor(Guid eventId) =>
        api.ReservationsQueue.Sent.Where(m =>
            m.Subject == nameof(ReservationCreated)
            && m.Body.ToObjectFromJson<ReservationCreated>(ApiFixture.Json)!.EventId == eventId);

    private Task<List<OutboxMessage>> OutboxFor(Guid reservationId) =>
        api.WithDbAsync(db => db.OutboxMessages
            .Where(m => m.MessageId.StartsWith(reservationId.ToString()))
            .OrderBy(m => m.Id)
            .ToListAsync());

    // only ReservationCreated names the event, so count the reservations whose event it is
    private Task<int> OutboxCountFor(Guid eventId) =>
        api.WithDbAsync(db => db.OutboxMessages.CountAsync(m => db.Reservations
            .Any(r => m.MessageId.StartsWith(r.Id.ToString())
                      && db.Seats.Any(s => s.Id == r.SeatId && s.EventId == eventId))));

    private Task<HttpResponseMessage> ReserveAsync(Guid eventId, int seatNumber, string email) =>
        api.Client.PostAsJsonAsync($"/events/{eventId}/reservations",
            new ReserveSeatRequest(seatNumber, email), TestContext.Current.CancellationToken);
}
