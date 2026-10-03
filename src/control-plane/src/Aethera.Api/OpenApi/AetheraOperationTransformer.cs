using System.Text.Json.Nodes;
using Aethera.Api.Http;
using Aethera.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aethera.Api.OpenApi;

/// <summary>
/// Per-operation contract derived from endpoint metadata: security requirements, <c>x-required-scope</c>, and the ProblemDetails responses
/// the shared plumbing can produce (401/403 when authorization applies, 422 when <c>.Validate&lt;T&gt;()</c> is used, 500 always).
/// </summary>
public sealed class AetheraOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var document = context.Document;
        var requiresAuth = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();

        if (requiresAuth)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(AetheraDocumentTransformer.CookieScheme, document)] = [] });
            operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(AetheraDocumentTransformer.BearerScheme, document)] = [] });
            AddProblem(operation, document, "401", "Not authenticated (auth.unauthenticated).", AetheraDocumentTransformer.ProblemDetailsSchema);
            AddProblem(operation, document, "403", "Role or token scope insufficient (auth.forbidden, auth.insufficient_scope).", AetheraDocumentTransformer.ProblemDetailsSchema);
        }

        if (metadata.OfType<RequiredScopeMetadata>().LastOrDefault() is { } scope)
        {
            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            operation.Extensions["x-required-scope"] = new JsonNodeExtension(JsonValue.Create(scope.Scope)!);
        }

        if (metadata.OfType<ValidationMetadata>().Any())
            AddProblem(operation, document, "422", "Validation failed (validation.failed).", AetheraDocumentTransformer.ValidationProblemSchema);

        AddProblem(operation, document, "500", "Unexpected error (internal.error).", AetheraDocumentTransformer.ProblemDetailsSchema);
        return Task.CompletedTask;
    }

    private static void AddProblem(OpenApiOperation operation, OpenApiDocument? document, string status, string description, string schema)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.ContainsKey(status)) return;
        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(schema, document) },
            },
        };
    }
}
