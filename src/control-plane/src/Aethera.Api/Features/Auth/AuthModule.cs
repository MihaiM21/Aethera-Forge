// Owned by WP1.1 (Auth & access). Only WP1.1 edits this file and the Aethera.Api/Features/Auth and Aethera.Infrastructure/Auth folders.
// Program.cs already calls AddAuth and MapAuth; do not edit it.
using Aethera.Api.Security;
using Aethera.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Features.Auth;

public static class AuthModule
{
    /// <summary>Registers authentication handlers, cookie/CSRF options, password hasher, token and session services.</summary>
    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration)
    {
        // Bound lazily from the final configuration (tests and hosts may add settings after Program.cs ran).
        services.AddOptions<AuthOptions>().Configure<IConfiguration>((options, root) => root.GetSection(AuthOptions.SectionName).Bind(options));

        // The default scheme "Aethera" (policy scheme), role/scope policies, ProblemDetails 401/403 and ICurrentActor are already
        // registered by AddAetheraSecurity. Add only the two handlers it forwards to.
        var authentication = services.AddAuthentication();
        authentication.AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(AetheraAuthSchemes.Session, null);
        authentication.AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(AetheraAuthSchemes.Token, null);

        services.TryAddSingleton<PasswordService>();
        services.TryAddSingleton<SessionCookies>();
        services.TryAddSingleton<LoginRateLimiter>();
        services.TryAddScoped<SessionStore>();
        services.TryAddScoped<ApiTokenStore>();
        services.TryAddScoped<AuthService>();
        services.TryAddScoped<UserService>();
        services.TryAddScoped<ApiTokenService>();
        return services;
    }

    /// <summary>
    /// Maps /auth/*, /users and /api-tokens (group is /api/v1, authorization is required unless an endpoint says AllowAnonymous), and adds
    /// the CSRF check to the whole group.
    /// </summary>
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder api)
    {
        // CSRF for every endpoint of /api/v1 without touching Program.cs: a group convention applies to the endpoints mapped into the group
        // before and after this call, including those of the other feature modules.
        if (api is not IEndpointConventionBuilder group)
            throw new InvalidOperationException("MapAuth must be given the /api/v1 route group so the CSRF filter can cover it.");
        group.AddEndpointFilter<IEndpointConventionBuilder, CsrfEndpointFilter>();

        api.MapAuthEndpoints();
        api.MapUserEndpoints();
        api.MapApiTokenEndpoints();
        return api;
    }
}
