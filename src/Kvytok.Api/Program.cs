using System.Reflection;
using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.AddSqlServerDbContext<KvytokDbContext>("kvytok");
builder.Services.AddEndpoints(typeof(Program).Assembly);

var app = builder.Build();

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
