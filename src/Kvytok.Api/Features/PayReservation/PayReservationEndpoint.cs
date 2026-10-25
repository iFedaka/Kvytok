using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Kvytok.Contracts;
using Kvytok.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Api.Features.PayReservation;

public sealed record PayReservationRequest(string PaymentReference);

/// <summary>
/// The webhook a payment provider would call, with the provider removed. It reports that a
/// payment happened and nothing more: whether that confirms the reservation is the worker's
/// decision, because only the worker sees it race against the expiry.
/// Deliberately unauthenticated, so anyone reading the API reference can run the demo: the data
/// is test data, and a caller can only report a payment, not decide what it means.
/// </summary>
public sealed class PayReservationEndpoint : IEndpoint
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);
    private const int MaxReferenceLength = 64;

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("/reservations/{reservationId:guid}/payments", Handle)
           .WithName("PayReservation")
           .WithTags("Reservations")
           .WithSummary("Reports a payment for a reservation. The worker confirms it asynchronously, unless the hold expired first.");

    private static async Task<Results<Accepted, ValidationProblem, NotFound, ProblemHttpResult>> Handle(
        Guid reservationId,
        PayReservationRequest request,
        KvytokDbContext db,
        ServiceBusSender reservationsQueue,
        ILogger<PayReservationEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var reference = request.PaymentReference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > MaxReferenceLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.PaymentReference)] = [$"PaymentReference is required, at most {MaxReferenceLength} characters."],
            });
        }

        // A read, not a decision: it only spares the queue a message for a reservation that never existed
        if (!await db.Reservations.AnyAsync(r => r.Id == reservationId, cancellationToken))
            return TypedResults.NotFound();

        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(
            new PaymentReceived(reservationId, reference), JsonSerializerOptions.Web))
        {
            MessageId = $"{reservationId}:payment:{reference}",
            Subject = nameof(PaymentReceived),
            ContentType = "application/json",
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PublishTimeout);
            await reservationsQueue.SendMessageAsync(message, timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Payment {PaymentReference} for reservation {ReservationId} was not published",
                reference, reservationId);
            // Nothing is committed yet, so unlike a reservation this can fail honestly:
            // a provider retries a webhook that did not return 2xx.
            return TypedResults.Problem(
                title: "The payment could not be recorded. Try again.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.Accepted($"/reservations/{reservationId}");
    }
}
