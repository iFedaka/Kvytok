using System.Text.Json.Nodes;
using Kvytok.Api.Infrastructure;

namespace Kvytok.Api.IntegrationTests.Infrastructure;

public sealed class CorrelationIdOpenApiTests(ApiFixture api)
{
    [Fact]
    public async Task Documents_The_Header_On_Every_Operation_And_Every_Response()
    {
        var document = JsonNode.Parse(await api.Client.GetStringAsync("/openapi/v1.json",
            TestContext.Current.CancellationToken))!;

        var operations = document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject()
                .Where(op => op.Value!["responses"] is not null)
                .Select(op => (Name: $"{op.Key.ToUpperInvariant()} {path.Key}", Operation: op.Value!)))
            .ToList();
        Assert.NotEmpty(operations);

        Assert.All(operations, op =>
        {
            var parameter = Assert.Single(op.Operation["parameters"]!.AsArray(), p =>
                (string?)p!["name"] == CorrelationIdMiddleware.Header && (string?)p["in"] == "header");
            Assert.Equal(CorrelationIdMiddleware.Pattern, (string?)parameter!["schema"]?["pattern"]);
            Assert.All(op.Operation["responses"]!.AsObject(), response =>
                Assert.Equal(CorrelationIdMiddleware.Pattern,
                    (string?)response.Value!["headers"]?[CorrelationIdMiddleware.Header]?["schema"]?["pattern"]));
        });
    }
}
