using System.Text.Encodings.Web;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Auth;

/// <summary>
/// Scheme <c>Aethera.Token</c>: authenticates <c>Authorization: Bearer aeth_...</c> (and, for SignalR under <c>/hubs</c> only, the
/// <c>access_token</c> query parameter). A token acts as its owner: the principal carries the owner's current role, the token id and
/// the token's scopes cut down to what that role allows (<see cref="ScopeCeiling"/>). A revoked or expired token refines the 401 to
/// <c>auth.token_revoked</c> / <c>auth.token_expired</c>, but only once the secret itself matched; everything else, including a token
/// whose owner was deactivated, is plain <c>auth.unauthenticated</c>.
/// </summary>
public sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ReadCredential();
        if (presented is null) return AuthenticateResult.NoResult();

        if (!ApiTokenFormat.TryParse(presented, out var token)) return AuthenticateResult.Fail("Malformed API token.");

        // Resolved here, not injected: requests without a (well-formed) token must not need the database.
        var tokens = Context.RequestServices.GetRequiredService<ApiTokenStore>();
        var ip = Context.Connection.RemoteIpAddress?.ToString();
        var result = await tokens.ResolveAsync(token, ip, Context.RequestAborted);
        switch (result.Status)
        {
            case ApiTokenStatus.Revoked:
                Context.Items[AuthenticationSetup.FailureCodeItemKey] = AuthProblemCodes.TokenRevoked;
                return AuthenticateResult.Fail("API token revoked.");
            case ApiTokenStatus.Expired:
                Context.Items[AuthenticationSetup.FailureCodeItemKey] = AuthProblemCodes.TokenExpired;
                return AuthenticateResult.Fail("API token expired.");
            case not ApiTokenStatus.Valid:
                return AuthenticateResult.Fail("API token not recognized.");
        }

        var scopes = ScopeCeiling.Effective(result.Role, result.Scopes ?? []);
        var principal = AetheraPrincipal.Create(
            AetheraAuthSchemes.Token, result.UserId, result.OrganizationId, result.Role, result.TokenId, scopes);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AetheraAuthSchemes.Token));
    }

    private string? ReadCredential()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) return header[BearerPrefix.Length..].Trim();

        // SignalR clients that cannot set headers (WebSockets in browsers) pass access_token; only under /hubs (see AuthenticationSetup).
        if (Request.Path.StartsWithSegments("/hubs") && Request.Query.TryGetValue("access_token", out var query)) return query.ToString();
        return null;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = Context.Items.TryGetValue(AuthenticationSetup.FailureCodeItemKey, out var c) && c is string s ? s : ProblemCodes.Unauthenticated;
        return ApiProblems.Unauthenticated(code).ExecuteAsync(Context);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.Forbidden().ExecuteAsync(Context);
}
