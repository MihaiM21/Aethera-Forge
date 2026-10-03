using Aethera.Api.Security;
using Microsoft.AspNetCore.OpenApi;
using Scalar.AspNetCore;

namespace Aethera.Api.OpenApi;

/// <summary>
/// OpenAPI document <c>v1</c> (<c>Microsoft.AspNetCore.OpenApi</c>) and the Scalar reference UI (ADR 0003 section 9). Documents only the
/// <c>/api/v1</c> endpoints. The same document is written to <c>src/web/openapi/aethera.v1.json</c> at build time (see the csproj).
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentName = "v1";
    public const string DocsKey = "AETHERA_DOCS";
    public const string JsonRoute = "/api/openapi/{documentName}.json";
    public const string DocsRoute = "/api/docs";

    public static IServiceCollection AddAetheraOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = description =>
                description.RelativePath?.StartsWith("api/v1", StringComparison.OrdinalIgnoreCase) == true;
            options.AddDocumentTransformer<AetheraDocumentTransformer>();
            options.AddOperationTransformer<AetheraOperationTransformer>();
        });
        return services;
    }

    /// <summary>False when <c>AETHERA_DOCS=false</c>.</summary>
    public static bool DocsEnabled(IConfiguration configuration) =>
        !bool.TryParse(configuration[DocsKey], out var enabled) || enabled;

    /// <summary>
    /// Maps the document and, unless <c>AETHERA_DOCS=false</c>, the Scalar UI. With docs switched off the JSON stays reachable but
    /// requires at least the Viewer role.
    /// </summary>
    public static IEndpointRouteBuilder MapAetheraDocs(this WebApplication app)
    {
        var docs = DocsEnabled(app.Configuration);
        var json = app.MapOpenApi(JsonRoute);
        if (docs) json.AllowAnonymous(); else json.RequireRole(AetheraPolicies.Viewer);

        if (docs)
        {
            app.MapScalarApiReference(DocsRoute, options => options
                .WithTitle("Aethera API")
                .WithOpenApiRoutePattern(JsonRoute)
                .AddPreferredSecuritySchemes(AetheraDocumentTransformer.BearerScheme));
        }

        return app;
    }
}
