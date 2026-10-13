using System.Reflection;
using System.Text.Json.Serialization;
using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi(o => o.AddSchemaTransformer<DateTimeExampleSchemaTransformer>());
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
// throw on binding failures in every environment so BadRequestExceptionHandler can describe them
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.AddSqlServerDbContext<KvytokDbContext>("kvytok");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpoints(typeof(Program).Assembly);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<KvytokDbContext>();
    await db.Database.MigrateAsync();
}

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.MapGet("/", () => TypedResults.Ok(new
    {
        service = "Kvytok",
        version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        docs = "/scalar",
        openapi = "/openapi/v1.json",
        health = "/health"
    }))
   .ExcludeFromDescription();

app.MapEndpoints();

app.Run();
