using System.Net;
using System.Net.Http.Json;
using Kvytok.Api.Features.ReserveSeat;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;

namespace Kvytok.Api.IntegrationTests.Features;

// Every test that reserves a seat adds to the same counter, so this one runs alone to count exactly
[Collection(Collection)]
public sealed class ReservationMetricsTests(ApiFixture api)
{
    public const string Collection = "Reservation metrics";

    [Fact]
    public async Task Counts_A_Hold_And_A_Lost_Race_But_Not_A_Missing_Seat_Or_A_Bad_Request()
    {
        using var reservations = new MetricCollector<long>(
            api.Meters, ReservationMetrics.MeterName, ReservationMetrics.InstrumentName);
        var eventId = await api.CreateEventAsync();

        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(eventId, 7)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ReserveAsync(eventId, 7)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReserveAsync(eventId, 99)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReserveAsync(eventId, 0)).StatusCode);

        Assert.Collection(reservations.GetMeasurementSnapshot(),
            m => Assert.Equal((1, "held"), (m.Value, m.Tags[ReservationMetrics.OutcomeTag])),
            m => Assert.Equal((1, "seat_taken"), (m.Value, m.Tags[ReservationMetrics.OutcomeTag])));
    }

    [Fact]
    public void The_Meter_Is_The_One_OpenTelemetry_Exports()
    {
        // ServiceDefaults exports the meter named after the application, not this class's assembly.
        // The two agree only by convention; if they parted, the counter would still count, the
        // test above would still pass, and the dashboard would go quiet with nothing logged.
        // OutboxRelay's trace source is named the same way and traced the same way, so this guards
        // OutboxRelay.Send too. Proves the names agree, not that AddMeter is still called.
        var application = api.Services.GetRequiredService<IHostEnvironment>().ApplicationName;

        Assert.Equal(application, ReservationMetrics.MeterName);
    }

    [Theory]
    [InlineData("src/Kvytok.AppHost/dashboard.bicep")]
    [InlineData("infra/dashboard/dashboard.bicep")]
    public void The_Dashboard_Queries_The_Names_The_Api_Emits(string path)
    {
        // The tiles name the metric, its tag and the route in KQL strings; a rename on either side
        // leaves a tile that renders empty rather than one that fails. Both copies: the AppHost's is
        // what a local run deploys, the one in infra/ what azd provision deploys.
        var bicep = File.ReadAllText(Path.Combine(RepositoryRoot(), path));
        var reserveSeat = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "ReserveSeat");

        Assert.Contains($"\"{ReservationMetrics.InstrumentName}\"", bicep);
        Assert.Contains($"[\"{ReservationMetrics.OutcomeTag}\"]", bicep);
        Assert.Contains($"\"POST {reserveSeat.RoutePattern.RawText}\"", bicep);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Kvytok.slnx")))
            directory = directory.Parent ?? throw new InvalidOperationException("Kvytok.slnx not found above the test output.");
        return directory.FullName;
    }

    private Task<HttpResponseMessage> ReserveAsync(Guid eventId, int seatNumber) =>
        api.Client.PostAsJsonAsync($"/events/{eventId}/reservations",
            new ReserveSeatRequest(seatNumber, "holder@example.com"),
            TestContext.Current.CancellationToken);
}

[CollectionDefinition(ReservationMetricsTests.Collection, DisableParallelization = true)]
public sealed class ReservationMetricsCollection;
