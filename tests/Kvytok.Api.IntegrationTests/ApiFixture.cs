using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kvytok.Api.Features.CreateEvent;
using Kvytok.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(Kvytok.Api.IntegrationTests.ApiFixture))]

namespace Kvytok.Api.IntegrationTests;

/// <summary>
/// One SQL Server container and one in-process API for the whole test run.
/// Tests isolate themselves by creating their own event, so nothing is reset between them.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private WebApplicationFactory<Program> _factory = null!;

    // Fixed so expiry times can be asserted exactly. Never advanced: tests share it.
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _sql.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Development applies migrations on startup
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:kvytok", _sql.GetConnectionString());
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
        });

        Client = _factory.CreateClient();
    }

    public async Task<T> WithDbAsync<T>(Func<KvytokDbContext, Task<T>> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<KvytokDbContext>());
    }

    public async Task<Guid> CreateEventAsync(int seatCount = 10)
    {
        var response = await Client.PostAsJsonAsync("/events",
            new CreateEventRequest($"Test {Guid.NewGuid():N}", Clock.GetUtcNow().AddDays(30), seatCount));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CreateEventResponse>(Json);
        return body!.EventId;
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _sql.DisposeAsync();
    }
}
