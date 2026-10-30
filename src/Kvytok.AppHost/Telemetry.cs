using Aspire.Hosting.Azure;
using Azure.Provisioning.ApplicationInsights;

internal static class Telemetry
{
    private static readonly RoleDefinition MonitoringMetricsPublisher = new(
        ApplicationInsightsBuiltInRole.MonitoringMetricsPublisher.ToString(), nameof(MonitoringMetricsPublisher));

    /// <summary>
    /// Turns off ingestion by instrumentation key, so the connection string only says where telemetry
    /// goes and an Entra ID token decides whether it is accepted, as with Service Bus.
    /// </summary>
    public static IResourceBuilder<AzureApplicationInsightsResource> RequiringEntraId(
        this IResourceBuilder<AzureApplicationInsightsResource> insights) =>
        insights.ConfigureInfrastructure(infra =>
            infra.GetProvisionableResources().OfType<ApplicationInsightsComponent>().Single().IsDisableLocalAuth = true);

    /// <summary>
    /// Has <paramref name="app"/> send its telemetry to <paramref name="insights"/>, signed in as its own
    /// identity (the developer, locally), which is allowed to publish and nothing more.
    /// </summary>
    public static IResourceBuilder<ProjectResource> SendsTelemetryTo(
        this IResourceBuilder<ProjectResource> app, IResourceBuilder<AzureApplicationInsightsResource> insights) =>
        app.WithReference(insights)
            .WithAnnotation(new RoleAssignmentAnnotation(insights.Resource, new HashSet<RoleDefinition> { MonitoringMetricsPublisher }));
}
