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

// Shared secret the platform's probes send to /health and /alive; generated once, then kept in the azd environment
var healthProbeKey = builder.AddParameter(
    "health-probe-key", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);

builder
    .AddProject<Projects.Kvytok_Api>("api")
    .WithReference(db)
    .WaitForCompletion(migrator)
    .WithUrlForEndpoint("https", _ => new() { Url = "/scalar", DisplayText = "Scalar" })
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((infra, app) =>
    {
        app.Template.Scale.MinReplicas = 0;
        app.Template.Scale.MaxReplicas = 3;
        app.ExposeHealthChecksToProbes(infra, healthProbeKey);
    });
builder
    .AddProject<Projects.Kvytok_Worker>("worker")
    .WithReference(db)
    .WaitForCompletion(migrator)
    .PublishAsAzureContainerApp((infra, app) =>
    {
        app.Template.Scale.MinReplicas = 0;
        app.Template.Scale.MaxReplicas = 1;
    });

builder.Build().Run();
