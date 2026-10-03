using System.Net;
using System.Net.Http.Json;
using Aethera.Domain;

namespace Aethera.Api.Tests;

[Collection(TestApiCollection.Name)]
public sealed class ProblemDetailsTests(TestApiFixture fixture)
{
    private HttpClient Dev() => fixture.Factory.CreateClientAs(OrganizationRole.Developer);

    [Fact]
    public async Task ApiProblem_HasTheAdrShape()
    {
        var id = Guid.NewGuid();
        var response = await Dev().GetAsync($"/api/v1/_test/not-found/{id}");
        var body = await response.ReadJsonAsync();

        response.AssertProblem(body, 404, "application.not_found");
        Assert.Equal("Application not found", body.Str("title"));
        Assert.Contains(id.ToString(), body.Str("detail"));
        Assert.Equal($"/api/v1/_test/not-found/{id}", body.Str("instance"));
    }

    [Fact]
    public async Task Conflict_UsesTheGivenCode()
    {
        var response = await Dev().GetAsync("/api/v1/_test/conflict");
        response.AssertProblem(await response.ReadJsonAsync(), 409, "domain.already_exists");
    }

    [Fact]
    public async Task UnknownRoute_IsAProblemWithoutHtml()
    {
        var response = await Dev().GetAsync("/api/v1/does-not-exist");
        response.AssertProblem(await response.ReadJsonAsync(), 404, "route.not_found");
    }

    [Fact]
    public async Task ProblemJson_IsReturnedEvenWhenTheClientAcceptsOnlyHtml()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/does-not-exist");
        request.Headers.TryAddWithoutValidation("Accept", "text/html");
        var response = await Dev().SendAsync(request);
        response.AssertProblem(await response.ReadJsonAsync(), 404, "route.not_found");
    }

    [Fact]
    public async Task UnhandledException_Is500InternalErrorWithoutDetails()
    {
        var response = await Dev().GetAsync("/api/v1/_test/boom");
        var text = await response.Content.ReadAsStringAsync();
        var body = System.Text.Json.Nodes.JsonNode.Parse(text)!;

        response.AssertProblem(body, 500, "internal.error");
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("InvalidOperationException", text);
        Assert.DoesNotContain("stack", text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(body["stackTrace"]);
    }

    [Fact]
    public async Task DomainRuleViolation_Is422()
    {
        var response = await Dev().GetAsync("/api/v1/_test/rule");
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 422, "domain.rule_violation");
        Assert.Equal("Email address is invalid.", body.Str("detail"));
    }

    [Fact]
    public async Task ConcurrencyException_Is409ConcurrencyConflict()
    {
        var response = await Dev().GetAsync("/api/v1/_test/concurrency");
        var text = await response.Content.ReadAsStringAsync();
        response.AssertProblem(System.Text.Json.Nodes.JsonNode.Parse(text)!, 409, "concurrency.conflict");
        Assert.DoesNotContain("xmin", text);
    }

    [Fact]
    public async Task UniqueViolation_Is409ResourceConflictWithoutConstraintNames()
    {
        var response = await Dev().GetAsync("/api/v1/_test/unique");
        var text = await response.Content.ReadAsStringAsync();
        response.AssertProblem(System.Text.Json.Nodes.JsonNode.Parse(text)!, 409, "resource.conflict");
        Assert.DoesNotContain("ix_secret_name", text);
    }

    [Fact]
    public async Task MalformedJson_Is400RequestMalformed()
    {
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        var response = await Dev().PostAsync("/api/v1/_test/echo", content);
        response.AssertProblem(await response.ReadJsonAsync(), 400, "request.malformed");
    }

    [Fact]
    public async Task OversizedBody_Is413()
    {
        using var content = new StringContent("\"" + new string('x', 2 * 1024 * 1024) + "\"", System.Text.Encoding.UTF8, "application/json");
        var response = await Dev().PostAsync("/api/v1/_test/echo", content);
        response.AssertProblem(await response.ReadJsonAsync(), 413, "request.too_large");
    }

    [Fact]
    public async Task DeleteWithoutConfirm_Is428AndNamesTheExpectedValue()
    {
        var response = await Dev().DeleteAsync("/api/v1/_test/things/my-app");
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 428, "confirmation.required");
        Assert.Contains("confirm=my-app", body.Str("detail"));
        Assert.Equal("my-app", body.Str("expectedConfirmation"));

        var wrong = await Dev().DeleteAsync("/api/v1/_test/things/my-app?confirm=other");
        Assert.Equal(HttpStatusCode.PreconditionRequired, wrong.StatusCode);

        var ok = await Dev().DeleteAsync("/api/v1/_test/things/my-app?confirm=my-app");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Is401WithoutRedirect()
    {
        var response = await fixture.Factory.CreateAnonymousClient().GetAsync("/api/v1/_test/authenticated");
        var body = await response.ReadJsonAsync();

        response.AssertProblem(body, 401, "auth.unauthenticated");
        Assert.Null(response.Headers.Location);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task InsufficientRole_Is403Forbidden()
    {
        var response = await fixture.Factory.CreateClientAs(OrganizationRole.Viewer).GetAsync("/api/v1/_test/role/developer");
        response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.forbidden");
    }

    [Fact]
    public async Task HealthStillWorks()
    {
        var response = await fixture.Factory.CreateAnonymousClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.ReadJsonAsync()).Str("status"));
    }
}
