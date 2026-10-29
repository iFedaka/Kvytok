var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("aspire-env")
    .WithDashboard(false);

var sql = builder
    .AddAzureSqlServer("sql")
    .RunAsContainer();
var db  = sql.AddDatabase("kvytok");

// Applies EF Core migrations and exits; in Azure it is a manually triggered Container Apps job
var migrator = builder
    .AddProject<Projects.Kvytok_MigrationService>("migrator")
    .WithReference(db)
    .WaitFor(db)
    .PublishAsAzureContainerAppJob();

var serviceBus = builder.AddAzureServiceBus("messaging");
var reservationsQueue = serviceBus.AddServiceBusQueue("reservations");

// Shared secret the platform's probes send to /health and /alive; generated once, then kept in the azd environment
var healthProbeKey = builder.AddParameter(
    "health-probe-key", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);

var api = builder
    .AddProject<Projects.Kvytok_Api>("api")
    .WithReference(db)
    .WithReference(reservationsQueue)
    .WaitForCompletion(migrator)
    // This is the demo environment: nobody watches a screen for ten minutes.
    // The Api's own default is the ten minutes the README describes.
    .WithEnvironment("Reservation__HoldFor", "00:00:30")
    // Must stay well under the hold: a lost publish stays lost this long plus one drain
    .WithEnvironment("Outbox__Lease", "00:00:05")
    .WithUrlForEndpoint("https", _ => new() { Url = "/scalar", DisplayText = "Scalar" })
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((infra, app) =>
    {
        app.Template.Scale.MinReplicas = 0;
        app.Template.Scale.MaxReplicas = 3;
        app.ExposeHealthChecksToProbes(infra, healthProbeKey);
    });
var worker = builder
    .AddProject<Projects.Kvytok_Worker>("worker")
    .WithReference(db)
    .WithReference(reservationsQueue)
    .WaitForCompletion(migrator)
    .PublishAsAzureContainerApp((infra, app) =>
    {
        app.Template.Scale.MinReplicas = 0;
        app.Template.Scale.MaxReplicas = 1;
        app.ScaleOnQueue(infra, reservationsQueue);
    });

migrator.NarrowsToReadWrite(api, worker);

// Locally the services reach Azure as the developer (az or azd login). Managed identity
// exists only in Azure, and probing for it on a laptop can hang for minutes.
if (builder.ExecutionContext.IsRunMode)
{
    api.WithEnvironment("AZURE_TOKEN_CREDENTIALS", "dev");
    worker.WithEnvironment("AZURE_TOKEN_CREDENTIALS", "dev");
}

builder.Build().Run();
