using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddSqlServerDbContext<KvytokDbContext>("kvytok");

using var host = builder.Build();

// Started so telemetry is exported, then stopped so the process exits and dependents can proceed
await host.StartAsync();
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KvytokDbContext>();
    await db.Database.MigrateAsync();
}
await host.StopAsync();
