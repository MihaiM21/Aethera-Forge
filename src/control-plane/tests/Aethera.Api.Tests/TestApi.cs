using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aethera.Api.Tests;

public enum ThingStatus { InProgress, AgentUnavailable }

public sealed record CreateThing(string? Name, List<ThingDomain>? Domains, int Memory);

public sealed record ThingDomain(string? Host);

public sealed class CreateThingValidator : AbstractValidator<CreateThing>
{
    public CreateThingValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Memory).InclusiveBetween(6, 100);
        RuleForEach(x => x.Domains).ChildRules(d => d.RuleFor(x => x.Host).Must(h => h is { Length: > 0 } && !h.Contains(' '))
            .WithErrorCode("domain.invalid_host").WithMessage("Must be a valid DNS name."));
    }
}

public sealed record JsonSample(ThingStatus Status, string? Note, DateTimeOffset CreatedAt, DateTime Local, Guid Id);

public sealed record DictionarySample(Dictionary<string, string> Variables);

public sealed record WhoAmI(bool IsAuthenticated, Guid? UserId, Guid? ApiTokenId, Guid? OrganizationId, string? Role, string[] Scopes, string RequestId);

/// <summary>Throw-away endpoints under /api/v1/_test that exercise the shared plumbing. Each is named, as required of real endpoints.</summary>
public static class TestApi
{
    public static void Services(IServiceCollection services) =>
        services.AddScoped<IValidator<CreateThing>, CreateThingValidator>();

    public static void Map(IEndpointRouteBuilder api)
    {
        var t = api.MapGroup("/_test").WithTags("Test");

        // roles
        t.MapGet("/role/viewer", () => "ok").WithName("testRoleViewer").RequireRole(AetheraPolicies.Viewer);
        t.MapGet("/role/developer", () => "ok").WithName("testRoleDeveloper").RequireRole(AetheraPolicies.Developer);
        t.MapGet("/role/admin", () => "ok").WithName("testRoleAdmin").RequireRole(AetheraPolicies.Admin);
        t.MapGet("/role/owner", () => "ok").WithName("testRoleOwner").RequireRole(AetheraPolicies.Owner);

        // scopes
        t.MapGet("/scope/read", () => "ok").WithName("testScopeRead").RequireScope(Scopes.Read);
        t.MapPost("/scope/write", () => "ok").WithName("testScopeWrite").RequireScope(Scopes.Write);
        t.MapGet("/scope/secrets", () => "ok").WithName("testScopeSecrets").RequireScope(Scopes.SecretsRead);
        t.MapPost("/role-and-scope", () => "ok").WithName("testRoleAndScope")
            .RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Write);
        t.MapGet("/anonymous", () => "ok").WithName("testAnonymous").AllowAnonymous();
        t.MapGet("/authenticated", () => "ok").WithName("testAuthenticated");

        t.MapGet("/whoami", (ICurrentActor actor) => new WhoAmI(actor.IsAuthenticated, actor.UserId, actor.ApiTokenId,
                actor.OrganizationId, actor.Role?.ToString(), [.. actor.Scopes.Order()], actor.RequestId))
            .WithName("testWhoAmI");

        // validation
        t.MapPost("/validate", (CreateThing body) => Results.Ok(body)).WithName("testValidate").Validate<CreateThing>();

        // errors
        t.MapGet("/boom", () => ThrowBoom()).WithName("testBoom");
        t.MapGet("/rule", () => ThrowRule()).WithName("testRule");
        t.MapGet("/concurrency", () => ThrowConcurrency()).WithName("testConcurrency");
        t.MapGet("/unique", () => ThrowUnique()).WithName("testUnique");
        t.MapGet("/not-found/{id:guid}", (Guid id) => ApiProblems.NotFound("application", id)).WithName("testNotFound");
        t.MapGet("/conflict", () => ApiProblems.Conflict("domain.already_exists", "Taken.")).WithName("testConflict");
        t.MapDelete("/things/{slug}", (string slug, string? confirm) =>
            {
                Confirmation.Require(confirm, slug);
                return Results.NoContent();
            })
            .WithName("testDeleteThing");
        t.MapPost("/echo", (System.Text.Json.JsonElement body) => Results.Ok(body)).WithName("testEcho");

        // conventions
        t.MapGet("/json", () => new JsonSample(ThingStatus.AgentUnavailable, null,
                new DateTimeOffset(2026, 10, 3, 16, 7, 31, 482, TimeSpan.FromHours(2)),
                new DateTime(2026, 10, 3, 14, 7, 31, 482, DateTimeKind.Utc), Guid.Parse("0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55")))
            .WithName("testJson");
        t.MapGet("/json-dictionary", () => new DictionarySample(
                new Dictionary<string, string> { ["NODE_ENV"] = "production", ["X-Custom"] = "1", ["PascalCase"] = "p", ["camelCase"] = "c" }))
            .WithName("testJsonDictionary");
        t.MapGet("/page", (KeysetCursor cursor, [AsParameters] PageRequest page, string? sort) =>
            {
                var request = page.Validated();
                var order = SortSpec.Parse(sort, ["createdAt", "name"], "-createdAt");
                var context = order.Canonical + "|status=running";
                if (request.Cursor is not null) cursor.Decode(request.Cursor, context);
                return new Page<string>(Enumerable.Range(0, request.Limit).Select(i => "item" + i).ToList(),
                    cursor.Encode(new KeysetPosition(["2026-10-03T14:07:31.4820000Z"], Guid.Parse("0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55")), context));
            })
            .WithName("testPage");

        // audit (needs a database)
        t.MapPost("/audit", async (IAuditLog audit, CancellationToken ct) =>
            {
                await audit.RecordAsync("thing.created", "thing", Guid.Parse("0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55"),
                    new { name = "web", password = "hunter2", nested = new { apiKey = "k-123", ok = 1 }, list = new[] { new { Token = "t-1", port = 80 } } }, ct);
                return Results.NoContent();
            })
            .WithName("testAudit");
    }

    private static string ThrowBoom() => throw new InvalidOperationException("Host=db;Password=hunter2 stack secrets");

    private static string ThrowRule() => throw new DomainRuleException("Email address is invalid.");

    private static string ThrowConcurrency() => throw new DbUpdateConcurrencyException("row changed (xmin 12)");

    private static string ThrowUnique() => throw new DbUpdateException("save failed",
        new PostgresException("duplicate key value violates unique constraint \"ix_secret_name\"", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));
}

/// <summary>One factory (and, when AETHERA_TEST_DB is set, one database) shared by all convention tests.</summary>
public sealed class TestApiFixture : IDisposable
{
    public TestApiFixture()
    {
        Base = new AetheraApiFactory();
        Factory = Base.WithEndpoints(TestApi.Map, TestApi.Services);
    }

    public AetheraApiFactory Base { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class TestApiCollection : ICollectionFixture<TestApiFixture>
{
    public const string Name = "api";
}
