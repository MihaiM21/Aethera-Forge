using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests;

[Collection(TestApiCollection.Name)]
public sealed class OpenApiTests(TestApiFixture fixture)
{
    private async Task<JsonNode> DocumentAsync() =>
        await (await fixture.Factory.CreateAnonymousClient().GetAsync("/api/openapi/v1.json")).ReadJsonAsync();

    [Fact]
    public async Task Document_IsServed_WithSecuritySchemesAndSharedProblemSchemas()
    {
        var response = await fixture.Factory.CreateAnonymousClient().GetAsync("/api/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.ReadJsonAsync();

        Assert.Equal("cookie", doc["components"]!["securitySchemes"]!["cookieAuth"]!["in"]!.GetValue<string>());
        Assert.Equal("bearer", doc["components"]!["securitySchemes"]!["bearerAuth"]!["scheme"]!.GetValue<string>());
        var schemas = doc["components"]!["schemas"]!;
        Assert.NotNull(schemas["ProblemDetails"]!["properties"]!["code"]);
        Assert.NotNull(schemas["ProblemDetails"]!["properties"]!["traceId"]);
        Assert.NotNull(schemas["ValidationProblem"]!["properties"]!["errors"]);
        Assert.NotNull(schemas["FieldError"]);
    }

    [Fact]
    public async Task Operations_CarryOperationId_RequiredScope_Security_AndProblemResponses()
    {
        var doc = await DocumentAsync();
        var paths = doc["paths"]!;

        var write = paths["/api/v1/_test/scope/write"]!["post"]!;
        Assert.Equal("testScopeWrite", write["operationId"]!.GetValue<string>());
        Assert.Equal("write", write["x-required-scope"]!.GetValue<string>());
        Assert.Equal(2, write["security"]!.AsArray().Count);
        Assert.Equal("#/components/schemas/ProblemDetails",
            write["responses"]!["403"]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>());
        Assert.NotNull(write["responses"]!["401"]);
        Assert.NotNull(write["responses"]!["500"]);

        var viewer = paths["/api/v1/_test/role/viewer"]!["get"]!;
        Assert.Null(viewer["x-required-scope"]);

        var anonymous = paths["/api/v1/_test/anonymous"]!["get"]!;
        Assert.Null(anonymous["security"]);
        Assert.Null(anonymous["responses"]!["401"]);

        var validate = paths["/api/v1/_test/validate"]!["post"]!;
        Assert.Equal("#/components/schemas/ValidationProblem",
            validate["responses"]!["422"]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task Document_ContainsOnlyApiV1Endpoints()
    {
        var doc = await DocumentAsync();
        Assert.All(doc["paths"]!.AsObject().Select(p => p.Key), path => Assert.StartsWith("/api/v1/", path));
    }

    [Fact]
    public async Task Docs_AreServedByDefault()
    {
        var response = await fixture.Factory.CreateAnonymousClient().GetAsync("/api/docs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DocsDisabled_RemovesTheUi_AndProtectsTheJson()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("AETHERA_DOCS", "false"));

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateAnonymousClient().GetAsync("/api/docs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateAnonymousClient().GetAsync("/api/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClientAs(OrganizationRole.Viewer).GetAsync("/api/openapi/v1.json")).StatusCode);
    }
}

public sealed class OperationIdConventionTests
{
    [Fact]
    public void EveryApiV1Endpoint_HasAUniqueCamelCaseName()
    {
        using var factory = new AetheraApiFactory();
        var problems = ApiConventions.FindOperationIdProblems(factory.Services.GetRequiredService<EndpointDataSource>());
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheTestEndpoints_FollowTheConvention()
    {
        using var factory = new AetheraApiFactory();
        using var withTests = factory.WithEndpoints(TestApi.Map, TestApi.Services);
        var endpoints = withTests.Services.GetRequiredService<EndpointDataSource>();
        Assert.Empty(ApiConventions.FindOperationIdProblems(endpoints));
        Assert.Contains(endpoints.Endpoints, e => (e as RouteEndpoint)?.RoutePattern.RawText == "/api/v1/_test/validate");
    }

    private sealed class FakeSource(params Endpoint[] endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;

        public override Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() => Microsoft.Extensions.FileProviders.NullChangeToken.Singleton;
    }

    private static RouteEndpoint Endpoint(string pattern, string? name)
    {
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        if (name is not null) builder.Metadata.Add(new EndpointNameMetadata(name));
        return (RouteEndpoint)builder.Build();
    }

    [Fact]
    public void Checker_ReportsMissingDuplicateAndBadlyCasedNames_ForApiV1Only()
    {
        var source = new FakeSource(
            Endpoint("/api/v1/projects", "listProjects"),
            Endpoint("/api/v1/projects/{id}", null),
            Endpoint("/api/v1/applications", "listProjects"),
            Endpoint("/api/v1/servers", "List_Servers"),
            Endpoint("/health", null),
            Endpoint("/api/v1/ok", "getOk"));

        var problems = ApiConventions.FindOperationIdProblems(source);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("/api/v1/projects/{id}") && p.Contains("no endpoint name"));
        Assert.Contains(problems, p => p.Contains("/api/v1/applications") && p.Contains("reuses the name 'listProjects'"));
        Assert.Contains(problems, p => p.Contains("List_Servers") && p.Contains("camelCase"));
    }
}
