using System.Net.Http.Json;
using Aethera.Api.Http;
using Aethera.Domain;
using FluentValidation;
using FluentValidation.Results;

namespace Aethera.Api.Tests;

[Collection(TestApiCollection.Name)]
public sealed class ValidationTests(TestApiFixture fixture)
{
    [Fact]
    public async Task InvalidBody_Is422WithJsonPointerErrors()
    {
        var client = fixture.Factory.CreateClientAs(OrganizationRole.Developer);
        var response = await client.PostAsJsonAsync("/api/v1/_test/validate", new
        {
            name = "this-name-is-too-long",
            memory = 1,
            domains = new[] { new { host = "ok.example.com" }, new { host = "bad host" } },
        });
        var body = await response.ReadJsonAsync();

        response.AssertProblem(body, 422, "validation.failed");
        Assert.Equal("Validation failed", body.Str("title"));
        Assert.Equal("3 fields are invalid.", body.Str("detail"));

        var errors = body["errors"]!.AsArray().Select(e => (Pointer: e!.Str("pointer"), Code: e.Str("code"), Message: e.Str("message"))).ToList();
        Assert.Contains(errors, e => e is { Pointer: "/name", Code: "too_long" });
        Assert.Contains(errors, e => e is { Pointer: "/memory", Code: "range" });
        Assert.Contains(errors, e => e is { Pointer: "/domains/1/host", Code: "domain.invalid_host", Message: "Must be a valid DNS name." });
        Assert.All(body["errors"]!.AsArray(), e => Assert.Null(e!["parameter"]));
    }

    [Fact]
    public async Task EmptyName_IsRequired()
    {
        var client = fixture.Factory.CreateClientAs(OrganizationRole.Developer);
        var response = await client.PostAsJsonAsync("/api/v1/_test/validate", new { name = "", memory = 10 });
        var body = await response.ReadJsonAsync();

        response.AssertProblem(body, 422, "validation.failed");
        var error = Assert.Single(body["errors"]!.AsArray());
        Assert.Equal("/name", error!.Str("pointer"));
        Assert.Equal("required", error.Str("code"));
    }

    [Fact]
    public async Task ValidBody_ReachesTheHandler()
    {
        var client = fixture.Factory.CreateClientAs(OrganizationRole.Developer);
        var response = await client.PostAsJsonAsync("/api/v1/_test/validate", new { name = "web", memory = 10, domains = new[] { new { host = "a.example.com" } } });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Domains[0].Host", "/domains/0/host")]
    [InlineData("Resources.MemoryLimitBytes", "/resources/memoryLimitBytes")]
    [InlineData("Env[A/B].Value", "/env/A~1B/value")]
    [InlineData("Matrix[1][2]", "/matrix/1/2")]
    [InlineData("", "")]
    public void ToPointer_ConvertsFluentValidationPaths(string path, string expected) =>
        Assert.Equal(expected, ValidationErrorMapper.ToPointer(path));

    [Fact]
    public void QuerySource_ProducesParameterLocations()
    {
        var result = new ValidationResult([new ValidationFailure("Limit", "Must be between 1 and 200.") { ErrorCode = "InclusiveBetweenValidator" }]);
        var error = Assert.Single(ValidationErrorMapper.ToFieldErrors(result, ValidationSource.Query));
        Assert.Null(error.Pointer);
        Assert.Equal("limit", error.Parameter);
        Assert.Equal("range", error.Code);
    }
}
