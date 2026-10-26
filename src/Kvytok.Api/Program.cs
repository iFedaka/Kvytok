using System.Reflection;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Kvytok.Api.Features.ReserveSeat;
using Kvytok.Domain;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi(o => o.AddSchemaTransformer<DateTimeExampleSchemaTransformer>());
builder.Services.AddProblemDetails();
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
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpoints(typeof(Program).Assembly);

var app = builder.Build();

app.UseExceptionHandler();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();


app.UseHttpsRedirection();

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
