using System.Security.Claims;
using Aethera.Api.Http.Errors;
using Microsoft.AspNetCore.Authorization;

namespace Aethera.Api.Security;

/// <summary>
/// Endpoint metadata: a browser session that still has to enrol two-factor authentication (claim
/// <see cref="AetheraClaimTypes.TwoFactorEnrolmentRequired"/>) may call this endpoint. Only what enrolment needs carries it: who am I, the CSRF
/// token, sign out and the <c>/auth/two-factor</c> endpoints.
/// </summary>
public sealed class AllowDuringTwoFactorEnrolment
{
    public static readonly AllowDuringTwoFactorEnrolment Instance = new();
}

/// <summary>
/// Keeps a session that must enrol two-factor authentication (ADR 0013) away from everything else. Two layers, both reading the same claim and
/// metadata: <see cref="RoleScopeAuthorizationHandler"/> fails role requirements (so SignalR hubs, which have no endpoint filters, are covered),
/// and <see cref="TwoFactorEnrolmentFilter"/> on the <c>/api/v1</c> group catches endpoints without a role requirement. API tokens never carry
/// the claim.
/// </summary>
public static class TwoFactorEnrolmentGate
{
    /// <summary>Failure reason recorded by the authorization handler; the 403 writer turns it into <c>auth.two_factor_enrolment_required</c>.</summary>
    public const string Reason = "two_factor_enrolment_required";

    public static bool MustEnrol(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && user.HasClaim(AetheraClaimTypes.TwoFactorEnrolmentRequired, "true");

    public static bool IsAllowed(Endpoint? endpoint) =>
        endpoint?.Metadata.GetMetadata<AllowDuringTwoFactorEnrolment>() is not null
        || endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    /// <summary>Endpoint of the request being authorized (the resource is the <see cref="HttpContext"/> under endpoint routing).</summary>
    public static Endpoint? EndpointOf(object? resource) => resource switch
    {
        HttpContext http => http.GetEndpoint(),
        Endpoint endpoint => endpoint,
        _ => null,
    };

    /// <summary>Marks an endpoint as reachable by a session that still has to enrol two-factor authentication.</summary>
    public static TBuilder AllowDuringTwoFactorEnrolment<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(Security.AllowDuringTwoFactorEnrolment.Instance);
}

/// <summary>Endpoint filter on the whole <c>/api/v1</c> group (added by <c>MapAuth</c>), see <see cref="TwoFactorEnrolmentGate"/>.</summary>
public sealed class TwoFactorEnrolmentFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!TwoFactorEnrolmentGate.MustEnrol(http.User) || TwoFactorEnrolmentGate.IsAllowed(http.GetEndpoint())) return next(context);
        return ValueTask.FromResult<object?>(ApiProblems.TwoFactorEnrolmentRequired());
    }
}
