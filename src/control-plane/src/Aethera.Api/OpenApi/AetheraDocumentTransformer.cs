using System.Text.Json.Nodes;
using Aethera.Api.Http.Errors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aethera.Api.OpenApi;

/// <summary>Document-level contract: info, security schemes and the shared error schemas.</summary>
public sealed class AetheraDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string CookieScheme = "cookieAuth";
    public const string BearerScheme = "bearerAuth";
    public const string ProblemDetailsSchema = "ProblemDetails";
    public const string ValidationProblemSchema = "ValidationProblem";
    public const string FieldErrorSchema = "FieldError";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Aethera API",
            Version = "v1",
            Description = "REST API of the Aethera control plane. Conventions: docs/architecture/0003-api-conventions.md.",
        };
        document.Servers = []; // the origin that serves the document; keeps the generated file independent of the host

        var components = document.Components ??= new OpenApiComponents();

        components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        components.SecuritySchemes[CookieScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "__Host-aethera_session",
            Description = "Browser session. Unsafe methods must also send the X-CSRF-Token header (GET /auth/csrf).",
        };
        components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "aeth_<token>",
            Description = "API token: Authorization: Bearer aeth_... Its scopes are listed per operation as x-required-scope.",
        };

        components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        components.Schemas[FieldErrorSchema] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "One field-level problem. Exactly one of pointer (JSON Pointer into the body) or parameter (query/path) is set.",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["pointer"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "JSON Pointer (RFC 6901) to the offending body member, e.g. /domains/0/host." },
                ["parameter"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Name of the offending query or path parameter." },
                ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Short stable validator id: required, too_long, too_short, pattern, range, invalid_enum, not_unique, or a domain-specific code." },
                ["message"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
            Required = new HashSet<string> { "code", "message" },
        };
        components.Schemas[ProblemDetailsSchema] = ProblemSchema(withErrors: false, document);
        components.Schemas[ValidationProblemSchema] = ProblemSchema(withErrors: true, document);
        return Task.CompletedTask;
    }

    private static OpenApiSchema ProblemSchema(bool withErrors, OpenApiDocument document)
    {
        var properties = new Dictionary<string, IOpenApiSchema>
        {
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Always urn:aethera:problem:<code>." },
            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Short fixed phrase per code. Do not parse." },
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
            ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Occurrence-specific text for humans. Do not parse." },
            ["instance"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "The request path." },
            ["code"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Stable machine-readable code, area.reason. Clients branch on this. Resource-specific codes (<resource>.not_found, <resource>.already_exists) are open-ended.",
                Extensions = new Dictionary<string, IOpenApiExtension>
                {
                    ["x-known-codes"] = new JsonNodeExtension(new JsonArray(ProblemCodes.Known.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray())),
                },
            },
            ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "The X-Request-Id of the failed request." },
        };
        var required = new HashSet<string> { "type", "title", "status", "code", "traceId" };
        if (withErrors)
        {
            properties["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = new OpenApiSchemaReference(FieldErrorSchema, document),
            };
            required.Add("errors");
        }

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = withErrors ? "RFC 9457 problem with field-level errors (422, and 400 for invalid parameters)." : "RFC 9457 problem details.",
            Properties = properties,
            Required = required,
            AdditionalProperties = new OpenApiSchema(), // extension members such as requiredScope
        };
    }
}
