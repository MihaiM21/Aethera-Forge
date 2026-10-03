using Aethera.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Aethera.Api.Security;

/// <summary>Evaluates <see cref="MinimumRoleRequirement"/> and <see cref="ScopeRequirement"/> against the Aethera claims.</summary>
public sealed class RoleScopeAuthorizationHandler : IAuthorizationHandler
{
    /// <summary>Failure reason prefix recorded for a missing scope; the 403 writer turns it into <c>auth.insufficient_scope</c>.</summary>
    public const string MissingScopeReason = "missing_scope:";

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        var user = context.User;
        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case MinimumRoleRequirement role:
                    if (user.Identity?.IsAuthenticated == true && AetheraPrincipal.ReadRole(user) is { } actual && actual >= role.Minimum)
                        context.Succeed(requirement);
                    else
                        context.Fail(new AuthorizationFailureReason(this, $"role:{role.Minimum}"));
                    break;

                case ScopeRequirement scope:
                    if (!AetheraPrincipal.IsTokenAuthenticated(user))
                    {
                        // Sessions (and anonymous callers, who fail authentication elsewhere) are not scope-limited.
                        if (user.Identity?.IsAuthenticated == true) context.Succeed(requirement);
                    }
                    else if (Scopes.Satisfies(AetheraPrincipal.ReadScopes(user), scope.Scope))
                        context.Succeed(requirement);
                    else
                        context.Fail(new AuthorizationFailureReason(this, MissingScopeReason + scope.Scope));
                    break;
            }
        }

        return Task.CompletedTask;
    }
}
