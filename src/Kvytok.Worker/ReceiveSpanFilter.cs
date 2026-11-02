using System.Diagnostics;
using OpenTelemetry;

namespace Kvytok.Worker;

/// <summary>
/// Keeps the processor's receive calls out of the telemetry. Each one is a long poll that waits up to
/// a minute for the queue, so it measures idle time, not work; it sits in a trace of its own and only
/// links to the messages it returned. Shown beside a message's trace, it stretches the timeline to its
/// own length and squeezes the actual handling into a sliver. ProcessMessage already covers the handling.
/// </summary>
internal sealed class ReceiveSpanFilter : BaseProcessor<Activity>
{
    private const string ReceiveOperation = "ServiceBusReceiver.Receive";

    public override void OnStart(Activity activity)
    {
        // Unrequested data is never handed to an exporter
        if (activity.OperationName == ReceiveOperation)
            activity.IsAllDataRequested = false;
    }
}
