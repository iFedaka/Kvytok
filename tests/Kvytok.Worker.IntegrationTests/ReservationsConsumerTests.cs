using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Kvytok.Contracts;
using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kvytok.Worker.IntegrationTests;

/// <summary>
/// How the consumer settles a message, with the real handler against the database and the broker
/// replaced by fakes that record each call, in order. A step is made to fail the way a poison
/// message does in production: a reservation Held on a seat that is not.
/// </summary>
public sealed class ReservationsConsumerTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RetryOptions _retry = new()
    {
        MaxAttempts = 5,
        FirstDelay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromMinutes(1),
    };
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly List<string> _calls = [];

    [Fact]
    public async Task A_Failed_Step_Is_Sent_Again_Later_Before_The_Original_Is_Completed()
    {
        var message = await FailingPaymentAsync(priorAttempts: 0, deliveryCount: 1);
        var queue = new FakeSender(_calls);

        await ConsumerFor(queue).ProcessMessageAsync(Args(message, new FakeReceiver(_calls)));

        Assert.Equal(["send", "complete"], _calls);
        var copy = Assert.Single(queue.Sent);
        Assert.Equal(message.MessageId, copy.MessageId);
        Assert.Equal(message.Subject, copy.Subject);
        Assert.Equal(message.Body.ToString(), copy.Body.ToString());
        Assert.Equal(1, copy.ApplicationProperties[ReservationsConsumer.PriorAttemptsProperty]);
        // the first wait, within its jitter
        Assert.InRange(copy.ScheduledEnqueueTime - _clock.GetUtcNow(), TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(2.4));
    }

    [Fact]
    public async Task A_Completion_That_Fails_After_The_Copy_Went_Out_Leaves_The_Original_To_Come_Back()
    {
        var message = await FailingPaymentAsync(priorAttempts: 0, deliveryCount: 1);
        var queue = new FakeSender(_calls);

        await Assert.ThrowsAsync<ServiceBusException>(() =>
            ConsumerFor(queue).ProcessMessageAsync(Args(message, new FakeReceiver(_calls) { FailCompletion = true })));

        // The processor abandons the original on the exception, so there are two copies now;
        // whichever runs second is skipped by the message key
        Assert.Equal(["send", "complete"], _calls);
        Assert.Single(queue.Sent);
    }

    [Fact]
    public async Task The_Last_Attempt_Dead_Letters_With_The_Count_Reset_For_A_Replay()
    {
        var message = await FailingPaymentAsync(priorAttempts: 4, deliveryCount: 1);
        var receiver = new FakeReceiver(_calls);
        var queue = new FakeSender(_calls);

        await ConsumerFor(queue).ProcessMessageAsync(Args(message, receiver));

        Assert.Equal(["dead-letter"], _calls);
        Assert.Empty(queue.Sent);
        Assert.Equal("RetriesExhausted", receiver.DeadLetterReason);
        Assert.StartsWith(nameof(InvalidOperationException), receiver.DeadLetterDescription);
        Assert.Equal(0, receiver.ModifiedProperties![ReservationsConsumer.PriorAttemptsProperty]);
        Assert.Equal(5, receiver.ModifiedProperties![ReservationsConsumer.FailedAttemptsProperty]);
    }

    [Fact]
    public async Task A_Delivery_That_Crashed_The_Worker_Counts_As_An_Attempt()
    {
        // Two copies failed before this one, and this one has been delivered twice already
        // without being settled: this is the fifth try, so the last
        var message = await FailingPaymentAsync(priorAttempts: 2, deliveryCount: 3);
        var receiver = new FakeReceiver(_calls);

        await ConsumerFor(new FakeSender(_calls)).ProcessMessageAsync(Args(message, receiver));

        Assert.Equal(["dead-letter"], _calls);
        Assert.Equal(5, receiver.ModifiedProperties![ReservationsConsumer.FailedAttemptsProperty]);
    }

    [Fact]
    public async Task A_Message_No_Retry_Can_Help_Is_Dead_Lettered_At_Once()
    {
        var message = Message("SomethingElse", "{}", priorAttempts: 0, deliveryCount: 1);
        var receiver = new FakeReceiver(_calls);
        var queue = new FakeSender(_calls);

        await ConsumerFor(queue).ProcessMessageAsync(Args(message, receiver));

        Assert.Equal(["dead-letter"], _calls);
        Assert.Empty(queue.Sent);
        Assert.Equal("UnknownSubject", receiver.DeadLetterReason);
        Assert.Null(receiver.ModifiedProperties);
    }

    [Fact]
    public async Task A_Step_Cut_Off_By_Shutdown_Is_Left_For_The_Processor_To_Abandon()
    {
        var message = await FailingPaymentAsync(priorAttempts: 0, deliveryCount: 1);
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConsumerFor(new FakeSender(_calls)).ProcessMessageAsync(Args(message, new FakeReceiver(_calls), shutdown.Token)));

        // not the message's fault: no attempt spent, nothing settled
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_Step_That_Succeeds_Is_Left_For_The_Processor_To_Complete()
    {
        var reservation = await database.SeedHeldAsync();
        var message = Message(nameof(PaymentReceived), Json(new PaymentReceived(reservation.Id, "pay_ok")), 0, 1);

        await ConsumerFor(new FakeSender(_calls)).ProcessMessageAsync(Args(message, new FakeReceiver(_calls)));

        Assert.Empty(_calls);
        Assert.Equal((ReservationStatus.Confirmed, SeatStatus.Booked), await database.StatusOfAsync(reservation));
    }

    /// <summary>A payment for a reservation Held on a free seat, which the lifecycle refuses every time.</summary>
    private async Task<ServiceBusReceivedMessage> FailingPaymentAsync(int priorAttempts, int deliveryCount)
    {
        var reservation = await database.SeedHeldAsync();
        await using (var db = database.CreateDb())
        {
            await db.Seats.Where(s => s.Id == reservation.SeatId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SeatStatus.Free), Ct);
        }

        return Message(
            nameof(PaymentReceived), Json(new PaymentReceived(reservation.Id, "pay_poison")), priorAttempts, deliveryCount);
    }

    private static ServiceBusReceivedMessage Message(string subject, string body, int priorAttempts, int deliveryCount) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            BinaryData.FromString(body),
            messageId: Guid.NewGuid().ToString(),
            subject: subject,
            properties: priorAttempts == 0
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { [ReservationsConsumer.PriorAttemptsProperty] = priorAttempts },
            deliveryCount: deliveryCount);

    private static string Json<T>(T body) => JsonSerializer.Serialize(body, JsonSerializerOptions.Web);

    private static ProcessMessageEventArgs Args(
        ServiceBusReceivedMessage message, ServiceBusReceiver receiver, CancellationToken? cancellationToken = null) =>
        new(message, receiver, cancellationToken ?? Ct);

    private ReservationsConsumer ConsumerFor(ServiceBusSender queue)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddScoped(_ => database.CreateDb())
            .AddScoped<ReservationLifecycle>()
            .AddScoped<ReservationMessageHandler>()
            .BuildServiceProvider();

        return new ReservationsConsumer(
            new FakeProcessor(),
            queue,
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(_retry),
            _clock,
            NullLogger<ReservationsConsumer>.Instance);
    }

    private sealed class FakeProcessor : ServiceBusProcessor;

    private sealed class FakeSender(List<string> calls) : ServiceBusSender
    {
        public List<ServiceBusMessage> Sent { get; } = [];

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            calls.Add("send");
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiver(List<string> calls) : ServiceBusReceiver
    {
        public bool FailCompletion { get; init; }
        public string? DeadLetterReason { get; private set; }
        public string? DeadLetterDescription { get; private set; }
        public IDictionary<string, object>? ModifiedProperties { get; private set; }

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        {
            calls.Add("complete");
            return FailCompletion
                ? Task.FromException(new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost))
                : Task.CompletedTask;
        }

        public override Task DeadLetterMessageAsync(
            ServiceBusReceivedMessage message, string deadLetterReason, string? deadLetterErrorDescription = null,
            CancellationToken cancellationToken = default) =>
            DeadLetter(null, deadLetterReason, deadLetterErrorDescription);

        public override Task DeadLetterMessageAsync(
            ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify, string deadLetterReason,
            string? deadLetterErrorDescription = null, CancellationToken cancellationToken = default) =>
            DeadLetter(propertiesToModify, deadLetterReason, deadLetterErrorDescription);

        private Task DeadLetter(IDictionary<string, object>? properties, string reason, string? description)
        {
            calls.Add("dead-letter");
            ModifiedProperties = properties;
            DeadLetterReason = reason;
            DeadLetterDescription = description;
            return Task.CompletedTask;
        }
    }
}
