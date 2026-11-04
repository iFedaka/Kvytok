using System.Net;
using System.Net.Http.Json;
using Kvytok.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Kvytok.Api.IntegrationTests.Infrastructure;

public sealed class CorrelationIdTests(ApiFixture api)
{
    [Fact]
    public async Task Echoes_The_Callers_Id()
    {
        var id = $"test-{Guid.NewGuid():N}";

        HttpResponseMessage response = await SendAsync("/", id);

        Assert.Equal(id, CorrelationIdOf(response));
    }

    [Fact]
    public async Task Gives_A_Request_Without_One_Its_Trace_Id()
    {
        var response = await SendAsync("/", id: null);

        Assert.Matches("^[0-9a-f]{32}$", CorrelationIdOf(response));
    }

    [Theory]
    [InlineData("has spaces in it")]
    [InlineData("semi;colon")]
    [InlineData("<script>")]
    public async Task Replaces_An_Id_It_Would_Not_Write_Into_A_Log(string unusable)
    {
        var response = await SendAsync("/", unusable);

        Assert.Matches("^[0-9a-f]{32}$", CorrelationIdOf(response));
    }

    [Fact]
    public async Task Replaces_An_Id_Longer_Than_64_Characters()
    {
        var response = await SendAsync("/", new string('a', 65));

        Assert.Matches("^[0-9a-f]{32}$", CorrelationIdOf(response));
    }

    // Values no HTTP request can carry, so the rule is asked directly
    [Theory]
    [InlineData("abc\n", false)] // '$' matches before a final newline: only the full-length check refuses this
    [InlineData("ab\nc", false)]
    [InlineData("abc\r\n", false)]
    [InlineData("", false)]
    [InlineData("abc", true)]
    [InlineData("order-42_retry.1:a", true)]
    public void Keeps_Only_An_Id_Matching_The_Whole_Pattern(string id, bool usable) =>
        Assert.Equal(usable, CorrelationIdMiddleware.IsUsable(id));

    [Fact]
    public async Task Puts_The_Id_On_An_Unhandled_Exceptions_Problem()
    {
        var id = $"test-{Guid.NewGuid():N}";

        var response = await SendAsync(ThrowingEndpoint.Path, id);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(id, CorrelationIdOf(response));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal(id, problem!.Extensions["correlationId"]?.ToString());
    }

    [Fact]
    public async Task Logs_The_Unhandled_Exception_With_The_Id()
    {
        var id = $"test-{Guid.NewGuid():N}";

        await SendAsync(ThrowingEndpoint.Path, id);

        var error = Assert.Single(api.Logs.GetSnapshot(), r =>
            r.Exception?.Message == ThrowingEndpoint.Message && HasCorrelationId(r.Scopes, id));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, error.Level);
    }

    [Fact]
    public async Task Puts_The_Id_On_A_Validation_Problem()
    {
        var id = $"test-{Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(new { name = "", startsAt = "2020-01-01T00:00:00Z", seatCount = 0 }),
        };
        request.Headers.Add(CorrelationIdMiddleware.Header, id);

        var response = await api.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal(id, problem!.Extensions["correlationId"]?.ToString());
    }

    [Fact]
    public async Task Puts_The_Id_On_A_Hand_Built_Problem_In_The_Header_Only()
    {
        var eventId = await api.CreateEventAsync(seatCount: 1);
        await api.ReserveAsync(eventId);
        var id = $"test-{Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/events/{eventId}/reservations")
        {
            Content = JsonContent.Create(new { seatNumber = 1, holderEmail = "second@example.com" }),
        };
        request.Headers.Add(CorrelationIdMiddleware.Header, id);

        var response = await api.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(id, CorrelationIdOf(response));
        // The 409 serializes its own ProblemDetails, bypassing IProblemDetailsService
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.False(problem!.Extensions.ContainsKey("correlationId"));
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string? id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (id is not null)
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.Header, id);
        return await api.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string CorrelationIdOf(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.Header));

    private static bool HasCorrelationId(IReadOnlyList<object?> scopes, string id) =>
        scopes.OfType<IEnumerable<KeyValuePair<string, object>>>()
            .Any(scope => scope.Any(p => p.Key == "CorrelationId" && Equals(p.Value, id)));
}
