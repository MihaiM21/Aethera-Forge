using System.Security.Claims;
using Aethera.Domain;

namespace Aethera.Api.Security;

/// <summary><see cref="ICurrentActor"/> over the current request's <see cref="ClaimsPrincipal"/> (claims: <see cref="AetheraClaimTypes"/>).</summary>
public sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    private HttpContext? Http => accessor.HttpContext;
    private ClaimsPrincipal? User => Http?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public Guid? UserId => IsAuthenticated ? AetheraPrincipal.ReadGuid(User!, AetheraClaimTypes.UserId) : null;

    public Guid? ApiTokenId => IsAuthenticated ? AetheraPrincipal.ReadGuid(User!, AetheraClaimTypes.TokenId) : null;

    public Guid? OrganizationId => IsAuthenticated ? AetheraPrincipal.ReadGuid(User!, AetheraClaimTypes.OrganizationId) : null;

    public OrganizationRole? Role => IsAuthenticated ? AetheraPrincipal.ReadRole(User!) : null;

    public IReadOnlySet<string> Scopes =>
        IsAuthenticated ? AetheraPrincipal.ReadScopes(User!) : (IReadOnlySet<string>)new HashSet<string>();

    public string? IpAddress => Http?.Connection.RemoteIpAddress?.ToString();

    public string RequestId => Http?.TraceIdentifier is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N");
}
