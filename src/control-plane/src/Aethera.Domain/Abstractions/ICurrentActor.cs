namespace Aethera.Domain;

/// <summary>
/// Who is performing the current operation. Implemented over the HTTP request (<c>HttpCurrentActor</c>) and, later,
/// by system actors for background work. Never throws: an anonymous or non-HTTP caller reports
/// <see cref="IsAuthenticated"/> = <c>false</c> and null ids.
/// </summary>
public interface ICurrentActor
{
    /// <summary>True when the caller proved an identity (browser session or API token).</summary>
    bool IsAuthenticated { get; }

    /// <summary>The user. Set for sessions and for tokens (a token acts on behalf of its owning user).</summary>
    Guid? UserId { get; }

    /// <summary>Set only when authenticated with an API token.</summary>
    Guid? ApiTokenId { get; }

    /// <summary>The organization the caller acts in (single-organization installs: always the default organization).</summary>
    Guid? OrganizationId { get; }

    /// <summary>The caller's role in <see cref="OrganizationId"/>. For tokens this is the owning user's role.</summary>
    OrganizationRole? Role { get; }

    /// <summary>
    /// Scopes granted to the API token (see <c>Scopes</c> in the API project). Empty for browser sessions, which are bound by
    /// role only (a session has no scope limitation, so absence of scopes must never be read as "no permission").
    /// </summary>
    IReadOnlySet<string> Scopes { get; }

    /// <summary>Remote address as seen by the server (after forwarded-header processing), or null outside HTTP.</summary>
    string? IpAddress { get; }

    /// <summary>
    /// Correlation id of the operation: the <c>X-Request-Id</c> of the request. Always non-empty. It equals the <c>traceId</c>
    /// of error bodies and is written to audit events and the logging scope.
    /// </summary>
    string RequestId { get; }
}
