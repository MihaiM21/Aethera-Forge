using System.Text.Encodings.Web;
using Aethera.Api.Security;
using Aethera.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Testing;

/// <summary>
/// Authentication handler for tests: trusts request headers instead of cookies/tokens, so endpoints can be tested before (or without) the
/// real handlers of WP1.1. It builds the principal with <see cref="AetheraPrincipal.Create"/>, i.e. with exactly the claims the real
/// handlers issue, so role policies, scope requirements and <c>ICurrentActor</c> behave as in production.
/// <para>No <see cref="RoleHeader"/> = anonymous (the pipeline then answers 401). A <see cref="TokenHeader"/> makes it a token principal
/// carrying <see cref="ScopesHeader"/>; without it, a session principal (bound by role only).</para>
/// <para>Do not set the headers by hand; use <c>CreateClientAs</c>.</para>
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string RoleHeader = "X-Test-Role";
    public const string UserHeader = "X-Test-User";
    public const string OrganizationHeader = "X-Test-Org";
    public const string TokenHeader = "X-Test-Token";
    public const string ScopesHeader = "X-Test-Scopes";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers;
        if (!Enum.TryParse<OrganizationRole>(headers[RoleHeader].ToString(), ignoreCase: true, out var role))
            return Task.FromResult(AuthenticateResult.NoResult());

        var userId = Guid.TryParse(headers[UserHeader], out var u) ? u : Guid.NewGuid();
        var organizationId = Guid.TryParse(headers[OrganizationHeader], out var o) ? o : Guid.NewGuid();
        Guid? tokenId = Guid.TryParse(headers[TokenHeader], out var t) ? t : null;
        var scopes = headers[ScopesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var principal = AetheraPrincipal.Create(AetheraAuthSchemes.Test, userId, organizationId, role, tokenId, scopes);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AetheraAuthSchemes.Test)));
    }
}
