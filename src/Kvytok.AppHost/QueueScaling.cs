using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning.AppContainers;

internal static class QueueScaling
{
    // How often the platform asks the queue for its length. Shorter than the default 30 s, because
    // a scheduled expiry that falls due on an idle app waits this long before anything wakes up.
    private const int PollingIntervalSeconds = 10;

    /// <summary>
    /// Wakes the app when <paramref name="queue"/> has messages ready, so it can sit at zero replicas
    /// without sleeping through them. Scheduled messages do not count until they fall due.
    /// </summary>
    public static void ScaleOnQueue(
        this ContainerApp app, AzureResourceInfrastructure infra, IResourceBuilder<AzureServiceBusQueueResource> queue)
    {
        var serviceBus = queue.Resource.Parent;

        // The identity Aspire made for this app, already granted access to Service Bus
        var target = ((AzureContainerAppResource)infra.AspireResource).TargetResource;
        var identity = target.Annotations.OfType<AppIdentityAnnotation>().Single()
            .IdentityResource.Id.AsProvisioningParameter(infra);

        app.Template.Scale.PollingInterval = PollingIntervalSeconds;
        app.Template.Scale.Rules.Add(new ContainerAppScaleRule
        {
            Name = $"{queue.Resource.QueueName}-queue",
            Custom = new ContainerAppCustomScaleRule
            {
                CustomScaleRuleType = "azure-servicebus",
                Identity = identity,
                Metadata =
                {
                    ["namespace"] = serviceBus.NameOutputReference.AsProvisioningParameter(infra),
                    ["queueName"] = queue.Resource.QueueName,
                    ["messageCount"] = "1",
                },
            },
        });
    }
}
