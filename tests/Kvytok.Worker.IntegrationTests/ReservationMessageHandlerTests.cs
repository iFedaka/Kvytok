using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Kvytok.Contracts;
using Kvytok.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kvytok.Worker.IntegrationTests;

public sealed class ReservationMessageHandlerTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_PaymentReceived_Message_Confirms()
    {
        var reservation = await database.SeedHeldAsync();

        await HandleAsync(Message(new PaymentReceived(reservation.Id, "pay_123")));

        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task A_ReservationExpiryDue_Message_Expires()
    {
        var reservation = await database.SeedHeldAsync();

        await HandleAsync(Message(new ReservationExpiryDue(reservation.Id)));

        Assert.Equal((ReservationStatus.Expired, SeatStatus.Free), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task A_ReservationCreated_Message_Changes_Nothing()
    {
        var reservation = await database.SeedHeldAsync();

        await HandleAsync(Message(
            new ReservationCreated(reservation.Id, Guid.NewGuid(), reservation.SeatId, 1, reservation.ExpiresAt)));

        Assert.Equal((ReservationStatus.Held, SeatStatus.Held), await database.StatusOfAsync(reservation));
    }

    [Fact]
    public async Task Refuses_A_Message_It_Does_Not_Know()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromString("{}"), subject: "SomethingElse");

        var refused = await Assert.ThrowsAsync<UnprocessableMessageException>(() => HandleAsync(message));
        Assert.Equal("UnknownSubject", refused.Reason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task Refuses_A_Body_That_Is_Not_Its_Contract(string body)
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            BinaryData.FromString(body), messageId: NewMessageId(), subject: nameof(PaymentReceived));

        var refused = await Assert.ThrowsAsync<UnprocessableMessageException>(() => HandleAsync(message));
        Assert.Equal("MalformedBody", refused.Reason);
    }

    private async Task HandleAsync(ServiceBusReceivedMessage message)
    {
        await using var db = database.CreateDb();
        var handler = new ReservationMessageHandler(DatabaseFixture.Lifecycle(db), NullLogger<ReservationMessageHandler>.Instance);
        await handler.HandleAsync(message, Ct);
    }

    private static ServiceBusReceivedMessage Message<T>(T body) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            BinaryData.FromObjectAsJson(body, JsonSerializerOptions.Web),
            messageId: NewMessageId(),
            subject: typeof(T).Name);

    private static string NewMessageId() => Guid.NewGuid().ToString();
}
