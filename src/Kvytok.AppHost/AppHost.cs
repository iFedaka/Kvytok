var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Kvytok_Api>("api");
builder.AddProject<Projects.Kvytok_Worker>("worker");

builder.Build().Run();
