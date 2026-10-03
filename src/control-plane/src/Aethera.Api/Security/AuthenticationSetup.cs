using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace Aethera.Api.Security;

/// <summary>Shared authentication/authorization plumbing. The credential handlers themselves belong to WP1.1 (see <c>AuthModule</c>).</summary>
public static class AuthenticationSetup
{
    /// <summary>
    /// <c>HttpContext.Items</c> key a handler can set (to a <see cref="ProblemCodes"/> value such as <c>auth.token_expired</c>) to refine
    /// the code of the 401 that follows a failed authentication. Defaults to <c>auth.unauthenticated</c>.
    /// </summary>
    public const string FailureCodeItemKey = "Aethera.AuthFailureCode";

    public static IServiceCollection AddAetheraSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActor, HttpCurrentActor>();

        // "Aethera" is the default scheme and only picks the real handler per request. WP1.1 registers the two targets.
        services.AddAuthentication(AetheraAuthSchemes.Default)
            .AddPolicyScheme(AetheraAuthSchemes.Default, "Aethera (session or API token)", options =>
                options.ForwardDefaultSelector = context => UsesBearerToken(context) ? AetheraAuthSchemes.Token : AetheraAuthSchemes.Session);

        services.AddAuthorization(AetheraPolicies.Register);
        services.AddSingleton<IAuthorizationHandler, RoleScopeAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        return services;
    }

    /// <summary>A bearer credential: the <c>Authorization: Bearer</c> header, or (SignalR only) the <c>access_token</c> query parameter under <c>/hubs</c>.</summary>
    public static bool UsesBearerToken(HttpContext context) =>
        context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        || (context.Request.Path.StartsWithSegments("/hubs") && context.Request.Query.ContainsKey("access_token"));
}

/// <summary>
/// Turns authorization outcomes into JSON problems, never redirects: 401 <c>auth.unauthenticated</c>, 403 <c>auth.forbidden</c>,
/// and 403 <c>auth.insufficient_scope</c> (with <c>requiredScope</c>) when a token lacks a scope.
/// </summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded) return next(context);

        if (authorizeResult.Challenged)
        {
            var code = context.Items.TryGetValue(AuthenticationSetup.FailureCodeItemKey, out var c) && c is string s ? s : ProblemCodes.Unauthenticated;
            return ApiProblems.Unauthenticated(code).ExecuteAsync(context);
        }

        var missingScope = authorizeResult.AuthorizationFailure?.FailureReasons
            .Select(r => r.Message)
            .FirstOrDefault(m => m.StartsWith(RoleScopeAuthorizationHandler.MissingScopeReason, StringComparison.Ordinal));
        var problem = missingScope is not null && !HasRoleFailure(authorizeResult)
            ? ApiProblems.InsufficientScope(missingScope[RoleScopeAuthorizationHandler.MissingScopeReason.Length..])
            : ApiProblems.Forbidden();
        return problem.ExecuteAsync(context);
    }

    private static bool HasRoleFailure(PolicyAuthorizationResult result) =>
        result.AuthorizationFailure?.FailureReasons.Any(r => r.Message.StartsWith("role:", StringComparison.Ordinal)) == true;
}

/// <summary>
/// Placeholder handler that authenticates nobody (<c>NoResult</c>), so the pipeline answers 401 before WP1.1 lands. WP1.1 replaces it in
/// <c>AuthModule.RegisterSessionScheme</c> / <c>RegisterTokenScheme</c>. It never challenges with a redirect.
/// </summary>
public sealed class PlaceholderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiProblems.Unauthenticated().ExecuteAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.Forbidden().ExecuteAsync(Context);
}
