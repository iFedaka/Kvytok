var builder = DistributedApplication.CreateBuilder(args);

var sql = builder
    .AddAzureSqlServer("sql")
    .RunAsContainer();
var db  = sql.AddDatabase("kvytok");

var api = builder
    .AddProject<Projects.Kvytok_Api>("api")
    .WithReference(db)
    .WaitFor(db);
builder
    .AddProject<Projects.Kvytok_Worker>("worker")
    .WithReference(db)
    .WaitFor(db)
    .WaitFor(api);

builder.Build().Run();
