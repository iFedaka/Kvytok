using System.Reflection;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Kvytok.Domain;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi(o => o.AddSchemaTransformer<DateTimeExampleSchemaTransformer>());
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
// throw on binding failures in every environment so BadRequestExceptionHandler can describe them
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.AddSqlServerDbContext<KvytokDbContext>("kvytok");

// The Azure SDK emits Service Bus spans only behind this switch; without it the send
// is missing from the trace. Must be set before the client is added.
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
// "reservations" is the queue reference, so the connection string carries the queue as EntityPath
string? reservationsQueue = null;
builder.AddAzureServiceBusClient("reservations", s => reservationsQueue = s.QueueOrTopicName);
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(reservationsQueue));
builder.Services.AddSingleton(TimeProvider.System);
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
