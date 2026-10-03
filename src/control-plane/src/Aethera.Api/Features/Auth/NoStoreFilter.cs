namespace Aethera.Api.Features.Auth;

/// <summary>
/// Adds <c>Cache-Control: no-store</c> to responses that carry credentials or session-bound values (a new API token, the CSRF token,
/// anything under <c>/auth</c>), so no proxy or browser cache keeps them.
/// </summary>
public sealed class NoStoreFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return next(context);
    }
}
