using System.Reflection;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Kvytok.Api.Features.ReserveSeat;
using Kvytok.Domain;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi(o => o
    .AddSchemaTransformer<DateTimeExampleSchemaTransformer>()
    .AddOperationTransformer<CorrelationIdOperationTransformer>());
// Problems written through IProblemDetailsService (validation, binding failures, an unhandled exception's 500)
// carry the correlation id in the body. Hand-built ones such as ReserveSeat's 409 do not; every response,
// those included, carries it in the X-Correlation-ID header.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
{
    if (CorrelationIdMiddleware.Get(context.HttpContext) is { } correlationId)
        context.ProblemDetails.Extensions["correlationId"] = correlationId;
});
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
// throw on binding failures in every environment so BadRequestExceptionHandler can describe them
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.AddSqlServerDbContext<KvytokDbContext>("kvytok",
    configureDbContextOptions: o => o.AddKvytokInterceptors());

// The Azure SDK emits Service Bus spans only behind this switch; without it the send
// is missing from the trace. Must be set before the client is added.
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
// "reservations" is the queue reference, so the connection string carries the queue as EntityPath
string? reservationsQueue = null;
builder.AddAzureServiceBusClient("reservations", s => reservationsQueue = s.QueueOrTopicName);
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(reservationsQueue));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<OutboxPublisher>();
builder.Services.AddOptions<OutboxOptions>()
    .BindConfiguration(OutboxOptions.Section)
    .Validate(o => o.Lease > OutboxOptions.MarkReserve,
        $"Outbox:Lease must be longer than {OutboxOptions.MarkReserve.TotalSeconds:0} second, e.g. 00:00:30.")
    // The longest a lost publish stays lost must fit inside the hold it is meant to end: claimable
    // after the lease, picked up within one drain, sent within one send timeout
    .Validate<IOptions<ReservationOptions>>(
        (o, reservation) => o.Lease + OutboxRelay.DrainEvery + o.SendTimeout < reservation.Value.HoldFor,
        $"Outbox:Lease, plus the relay's {OutboxRelay.DrainEvery.TotalSeconds:0}-second drain interval, plus one send " +
        $"(the lease or {OutboxOptions.PublishTimeout.TotalSeconds:0} seconds, whichever is shorter, less " +
        $"{OutboxOptions.MarkReserve.TotalSeconds:0} second) must be shorter than Reservation:HoldFor.")
    .ValidateOnStart();
// resolvable on its own, so a test can run a drain without waiting for the timer
builder.Services.AddSingleton<OutboxRelay>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay>());
builder.Services.AddOptions<ReservationOptions>()
    .BindConfiguration(ReservationOptions.Section)
    .Validate(o => o.HoldFor > TimeSpan.Zero, "Reservation:HoldFor must be a positive duration, e.g. 00:10:00.")
    .ValidateOnStart();
builder.Services.AddSingleton<ReservationMetrics>();
// In-process only. With no IDistributedCache registered, HybridCache has no second layer; adding
// one (builder.AddRedisDistributedCache) is the whole change. See "Caching" in the README.
builder.Services.AddHybridCache();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpoints(typeof(Program).Assembly);

var app = builder.Build();

// Outside the exception handler, so the error it logs for an unhandled exception carries the id
app.UseMiddleware<CorrelationIdMiddleware>();
// Exceptions no handler claims become a 500 problem with no exception details in it
app.UseExceptionHandler();

// Before the redirect: off Development it branches the health-probe port off, which must not be redirected
app.MapDefaultEndpoints();

app.UseHttpsRedirection();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapGet("/", () => TypedResults.Ok(new
    {
        service = "Kvytok",
        version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        docs = "/scalar",
        openapi = "/openapi/v1.json",
    }))
   .ExcludeFromDescription();

app.MapEndpoints();

app.Run();
