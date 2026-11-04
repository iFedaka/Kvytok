using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace Kvytok.Api.IntegrationTests.Infrastructure;

public sealed class ExceptionHandlingTests(ApiFixture api)
{
    [Fact]
    public async Task Answers_An_Unhandled_Exception_With_A_500_Problem()
    {
        var response = await api.Client.GetAsync(ThrowingEndpoint.Path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ApiFixture.Json,
            TestContext.Current.CancellationToken);
        Assert.Equal(500, problem!.Status);
    }

    // The message belongs in the log (see CorrelationIdTests), never in the response. Checked in
    // Development, the environment these tests run in, where adding error details looks harmless.
    [Fact]
    public async Task Keeps_The_Exception_Message_Out_Of_The_Response()
    {
        var response = await api.Client.GetAsync(ThrowingEndpoint.Path, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(ThrowingEndpoint.Message, body);
    }
}
