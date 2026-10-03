// Owned by WP1.1 (Auth & access). Only WP1.1 edits this file and the Aethera.Api/Features/Auth and Aethera.Infrastructure/Auth folders.
// Program.cs already calls AddAuth and MapAuth; do not edit it.
using Aethera.Api.Security;
using Microsoft.AspNetCore.Authentication;

namespace Aethera.Api.Features.Auth;

public static class AuthModule
{
    /// <summary>Registers authentication handlers, cookie/CSRF options, password hasher, token and session services.</summary>
    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration)
    {
        // The default scheme "Aethera" (policy scheme), role/scope policies, ProblemDetails 401/403 and ICurrentActor are already
        // registered by AddAetheraSecurity. Add only the two handlers it forwards to.
        var authentication = services.AddAuthentication();
        RegisterSessionScheme(authentication);
        RegisterTokenScheme(authentication);
        return services;
    }

    /// <summary>Maps /auth/* and /api-tokens (group is /api/v1, authorization is required unless an endpoint says AllowAnonymous).</summary>
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder api)
    {
        return api;
    }

    // TODO(WP1.1): replace with the cookie session handler ("__Host-aethera_session"). Must issue AetheraClaimTypes claims (see
    // AetheraPrincipal.Create), must answer failures as 401 JSON (no redirect to a login page), may set
    // HttpContext.Items[AuthenticationSetup.FailureCodeItemKey] to refine the 401 code.
    private static void RegisterSessionScheme(AuthenticationBuilder authentication) =>
        authentication.AddScheme<AuthenticationSchemeOptions, PlaceholderAuthenticationHandler>(AetheraAuthSchemes.Session, null);

    // TODO(WP1.1): replace with the "Authorization: Bearer aeth_..." handler (token principals carry AetheraClaimTypes.TokenId and one
    // AetheraClaimTypes.Scope claim per scope; the role claim is the owning user's role).
    private static void RegisterTokenScheme(AuthenticationBuilder authentication) =>
        authentication.AddScheme<AuthenticationSchemeOptions, PlaceholderAuthenticationHandler>(AetheraAuthSchemes.Token, null);
}
