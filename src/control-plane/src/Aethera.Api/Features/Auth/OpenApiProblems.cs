using Aethera.Api.OpenApi;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aethera.Api.Features.Auth;

/// <summary>
/// Documents the error responses an operation can return, with the <c>code</c> values that explain them, using the shared
/// <c>ProblemDetails</c> / <c>ValidationProblem</c> schemas of the document. (The global transformer already adds 401/403/422/500 in
/// general terms; this replaces those descriptions with the precise codes of the endpoint.)
/// </summary>
internal static class OpenApiProblems
{
    /// <param name="responses">Pairs of HTTP status and a description naming the problem codes, e.g. <c>(409, "auth.setup_completed")</c>.</param>
    public static RouteHandlerBuilder ProducesProblems(this RouteHandlerBuilder builder, params (int Status, string Description)[] responses) =>
        builder.AddOpenApiOperationTransformer((operation, context, _) =>
        {
            operation.Responses ??= new OpenApiResponses();
            foreach (var (status, description) in responses)
            {
                var schema = status == StatusCodes.Status422UnprocessableEntity
                    ? AetheraDocumentTransformer.ValidationProblemSchema
                    : AetheraDocumentTransformer.ProblemDetailsSchema;
                operation.Responses[status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new OpenApiResponse
                {
                    Description = description,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(schema, context.Document) },
                    },
                };
            }

            return Task.CompletedTask;
        });
}
