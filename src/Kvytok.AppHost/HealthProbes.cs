using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;

internal static class HealthProbes
{
    // Ingress routes only to the app's target port, so this one is reachable by the platform's probes alone
    private const int Port = 8081;

    // Must match the header MapDefaultEndpoints in ServiceDefaults checks
    private const string KeyHeader = "X-Health-Probe-Key";

    /// <summary>
    /// Has the app also listen on <see cref="Port"/>, serve /health and /alive only there and only to requests
    /// carrying <paramref name="key"/> (see MapDefaultEndpoints in ServiceDefaults), and probe them.
    /// </summary>
    public static void ExposeHealthChecksToProbes(
        this ContainerApp app, AzureResourceInfrastructure infra, IResourceBuilder<ParameterResource> key)
    {
        var container = app.Template.Containers.Single().Value!;
        var keyParameter = key.AsProvisioningParameter(infra);

        var httpPorts = container.Env.Single(e => e.Value!.Name.Value == "HTTP_PORTS").Value!;
        httpPorts.Value = BicepFunction.Interpolate($"{httpPorts.Value};{Port}");

        app.Configuration.Secrets.Add(new ContainerAppWritableSecret
        {
            Name = "healthchecks--probekey",
            Value = keyParameter,
        });
        container.Env.Add(new ContainerAppEnvironmentVariable { Name = "HealthChecks__Port", Value = Port.ToString() });
        container.Env.Add(new ContainerAppEnvironmentVariable
        {
            Name = "HealthChecks__ProbeKey",
            SecretRef = "healthchecks--probekey",
        });

        // Probe headers cannot reference secrets, so the key is also visible in the app's probe configuration
        container.Probes.Add(Probe(ContainerAppProbeType.Liveness, "/alive", keyParameter));
        container.Probes.Add(Probe(ContainerAppProbeType.Readiness, "/health", keyParameter));
    }

    private static ContainerAppProbe Probe(ContainerAppProbeType type, string path, ProvisioningParameter key) => new()
    {
        ProbeType = type,
        HttpGet = new ContainerAppHttpRequestInfo
        {
            Path = path,
            Port = Port,
            Scheme = ContainerAppHttpScheme.Http,
            HttpHeaders = { new ContainerAppHttpHeaderInfo { Name = KeyHeader, Value = key } },
        },
    };
}
