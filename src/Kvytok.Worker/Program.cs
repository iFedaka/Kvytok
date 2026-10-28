using Azure.Messaging.ServiceBus;
using Kvytok.Domain;
using Kvytok.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddSqlServerDbContext<KvytokDbContext>("kvytok",
    configureDbContextOptions: o => o.AddKvytokInterceptors());

// Must be set before the client is added, or the receive is missing from the trace
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
// "reservations" is the queue reference, so the connection string carries the queue as EntityPath
string? reservationsQueue = null;
builder.AddAzureServiceBusClient("reservations", s => reservationsQueue = s.QueueOrTopicName);
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateProcessor(reservationsQueue,
    new ServiceBusProcessorOptions
    {
        // More than one at a time on purpose: a payment and an expiry for the same reservation
        // can then be handled side by side, which is the race ReservationLifecycle exists for.
        MaxConcurrentCalls = 8,
    }));
// Retries go back to the queue they came from, as delayed copies
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(reservationsQueue));
builder.Services.AddOptions<RetryOptions>()
    .BindConfiguration(RetryOptions.Section)
    .Validate(o => o.MaxAttempts >= 1, "Retry:MaxAttempts must be at least 1.")
    .Validate(o => o.FirstDelay > TimeSpan.Zero && o.MaxDelay >= o.FirstDelay,
        "Retry:FirstDelay must be positive and no longer than Retry:MaxDelay.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<ReservationLifecycle>();
builder.Services.AddScoped<ReservationMessageHandler>();
builder.Services.AddHostedService<ReservationsConsumer>();

var host = builder.Build();
host.Run();
