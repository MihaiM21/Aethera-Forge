using Aethera.Api.Http.Errors;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace Aethera.Api.Http;

/// <summary>
/// Enforces the request body limit declared as endpoint metadata (<c>RequestSizeLimitAttribute</c> / <c>DisableRequestSizeLimit</c>;
/// the <c>/api/v1</c> group sets 1 MiB). Minimal-API endpoints do not apply that metadata by themselves. A declared
/// <c>Content-Length</c> over the limit is answered with 413 <c>request.too_large</c> immediately; for other bodies the server's body-size
/// feature is set so reading past the limit fails (mapped to the same problem by the exception handler).
/// </summary>
public sealed class BodySizeLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>() is { } metadata)
        {
            var limit = metadata.MaxRequestBodySize;
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;

            if (limit is { } max && context.Request.ContentLength > max)
            {
                await new ApiProblem(StatusCodes.Status413PayloadTooLarge, ProblemCodes.RequestTooLarge,
                    $"The request body exceeds the limit of {max} bytes.").ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
