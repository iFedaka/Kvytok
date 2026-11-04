using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kvytok.Worker.IntegrationTests;

public sealed class TelemetryNamesTests
{
    [Fact]
    public void The_Handlers_Trace_Source_Is_The_One_OpenTelemetry_Exports()
    {
        // ServiceDefaults traces the source named after the application, not the handler's assembly.
        // The two agree only by convention; if they parted, "Handle PaymentReceived" and "Handle
        // ReservationExpiryDue" would leave the trace with nothing failing and nothing logged.
        using var host = BuildWorkerHost();
        var application = host.Services.GetRequiredService<IHostEnvironment>().ApplicationName;

        Assert.Equal(application, ReservationMessageHandler.SourceName);
    }

    /// <summary>
    /// What WebApplicationFactory does for the Api: runs the Worker's own Program until its host is
    /// built, takes the host from the hosting diagnostics, and stops there, before anything connects.
    /// </summary>
    private static IHost BuildWorkerHost()
    {
        IHost? built = null;
        var inner = new List<IDisposable>();
        // The diagnostics are process-wide, but a host raises them on the thread that builds it.
        // Acting only where this flag flows keeps a test that builds a host of its own, in
        // parallel, from having it stopped by this one.
        var buildingHere = new AsyncLocal<bool>();
        using var listeners = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
                inner.Add(listener.Subscribe(new Observer<KeyValuePair<string, object?>>(e =>
                {
                    if (e.Key != "HostBuilt" || !buildingHere.Value)
                        return;
                    built = (IHost)e.Value!;
                    throw new StopBeforeRun();
                })));
        }));

        try
        {
            buildingHere.Value = true;
            var worker = typeof(ReservationMessageHandler).Assembly;
            worker.EntryPoint!.Invoke(null, [new[]
            {
                // The host names the application after the entry assembly: the Worker when it runs, this
                // test project here. WebApplicationFactory corrects the same thing for the Api. An
                // override in Program still wins over this; one in DOTNET_APPLICATIONNAME would not.
                $"--applicationName={worker.GetName().Name}",
                // Only parsed, never dialled: the host is stopped before it starts. The key is a
                // placeholder the client needs to build; the real namespace refuses keys altogether.
                "--ConnectionStrings:kvytok=Server=localhost;Database=kvytok;Integrated Security=true",
                "--ConnectionStrings:reservations=Endpoint=sb://localhost/;SharedAccessKeyName=test;SharedAccessKey=test;EntityPath=reservations",
            }]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is StopBeforeRun)
        {
        }
        finally
        {
            inner.ForEach(s => s.Dispose());
        }

        return built ?? throw new InvalidOperationException("The Worker's Program returned without building a host.");
    }

    private sealed class StopBeforeRun : Exception;

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
