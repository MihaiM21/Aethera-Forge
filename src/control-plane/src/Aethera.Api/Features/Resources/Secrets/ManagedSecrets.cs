using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Secrets;

/// <summary>The resource that owns a managed secret (<c>type</c> is <c>registry</c>, <c>server</c>, <c>gitCredential</c> or <c>service</c>).</summary>
public sealed record SecretOwnerResponse(string Type, Guid Id, string? Name);

/// <summary>
/// Secrets that belong to another resource (a registry password, a server's SSH key, a git credential, a generated service password) are
/// <em>managed</em>: <c>/secrets</c> lists them but cannot change them, and users cannot bind them as environment variables (ADR 0006).
/// </summary>
public static class ManagedSecrets
{
    /// <summary>Looks up the owners of the managed secrets among <paramref name="secrets"/> (secret id to owner; none for user secrets).</summary>
    public static async Task<Dictionary<Guid, SecretOwnerResponse>> OwnersAsync(AetheraDbContext db, IEnumerable<Secret> secrets, CancellationToken ct)
    {
        var managed = secrets.Where(s => s.IsManaged).ToList();
        var owners = new Dictionary<Guid, SecretOwnerResponse>();
        if (managed.Count == 0) return owners;
        var ids = managed.Select(s => s.Id).ToList();

        foreach (var r in await db.Registries.AsNoTracking().Where(r => r.PasswordSecretId != null && ids.Contains(r.PasswordSecretId.Value))
                     .Select(r => new { Secret = r.PasswordSecretId!.Value, r.Id, r.Name }).ToListAsync(ct))
            owners.TryAdd(r.Secret, new SecretOwnerResponse("registry", r.Id, r.Name));

        foreach (var s in await db.Servers.AsNoTracking().Where(s => s.SshCredentialSecretId != null && ids.Contains(s.SshCredentialSecretId.Value))
                     .OrderBy(s => s.CreatedAt).Select(s => new { Secret = s.SshCredentialSecretId!.Value, s.Id, s.Name }).ToListAsync(ct))
            owners.TryAdd(s.Secret, new SecretOwnerResponse("server", s.Id, s.Name));

        foreach (var c in await db.GitCredentials.AsNoTracking().Where(c => ids.Contains(c.SecretId))
                     .Select(c => new { Secret = c.SecretId, c.Id, c.Name }).ToListAsync(ct))
            owners.TryAdd(c.Secret, new SecretOwnerResponse("gitCredential", c.Id, c.Name));

        var workloadIds = managed.Where(s => s.Purpose == SecretPurpose.ServiceGenerated && s.WorkloadId is not null).Select(s => s.WorkloadId!.Value).ToList();
        if (workloadIds.Count > 0)
        {
            var services = await db.Workloads.AsNoTracking().Where(w => workloadIds.Contains(w.Id)).Select(w => new { w.Id, w.Name }).ToListAsync(ct);
            foreach (var secret in managed.Where(s => s.Purpose == SecretPurpose.ServiceGenerated && s.WorkloadId is not null))
            {
                if (services.FirstOrDefault(w => w.Id == secret.WorkloadId) is { } service)
                    owners.TryAdd(secret.Id, new SecretOwnerResponse("service", service.Id, service.Name));
            }
        }

        return owners;
    }

    /// <summary>409 <c>secret.managed</c> for PATCH, rotate and delete of a managed secret; the message names the endpoint to use instead.</summary>
    public static async Task EnsureUserSecretAsync(AetheraDbContext db, Secret secret, string action, CancellationToken ct)
    {
        if (!secret.IsManaged) return;
        var owner = (await OwnersAsync(db, [secret], ct)).GetValueOrDefault(secret.Id);
        var where = owner switch
        {
            { Type: "registry" } => $"Use PATCH /api/v1/registries/{owner.Id} (an Administrator changes the password there).",
            { Type: "server" } => $"Use PATCH /api/v1/servers/{owner.Id} to replace the SSH credential.",
            { Type: "gitCredential" } => $"Use PATCH /api/v1/git-credentials/{owner.Id}.",
            { Type: "service" } => $"It was generated for /api/v1/services/{owner.Id} and is managed through that service.",
            _ => "Use the endpoint of the resource it belongs to.",
        };
        throw new ApiProblemException(ApiProblems.Conflict(ResourceProblemCodes.SecretManaged,
            $"This secret is managed by another resource ({secret.Purpose.ToString().ToLowerInvariant()}) and cannot be {action} through /secrets. {where}")
            .WithExtension("purpose", secret.Purpose).WithExtension("managedBy", owner));
    }

    // ---- binding to environment variables -------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the caller may bind <paramref name="secret"/> to an environment variable of <paramref name="workload"/>
    /// (403 <c>secret.binding_forbidden</c>). A managed secret never; only a generated service secret to its own service. A user secret of
    /// organization scope only by an Administrator; narrower scopes by any Developer (the scope match itself is checked by the caller).
    /// </summary>
    public static void EnsureMayBind(ICurrentActor actor, Secret secret, Workload workload)
    {
        if (secret.IsManaged)
        {
            if (secret.Purpose == SecretPurpose.ServiceGenerated && secret.WorkloadId == workload.Id) return;
            throw new ApiProblemException(BindingForbidden(secret.Purpose == SecretPurpose.ServiceGenerated
                ? "This secret was generated for another service and can only be used by that service."
                : $"This secret is managed by another resource ({secret.Purpose.ToString().ToLowerInvariant()}) and cannot be used as an environment variable."));
        }

        if (secret.Scope == SecretScope.Organization && !actor.IsAdmin())
            throw new ApiProblemException(BindingForbidden(
                "Organization-wide secrets can only be bound by an Administrator. Create a secret scoped to the project, environment or application instead."));
    }

    /// <summary>An API token needs <c>secrets:write</c> to bind a secret (the <c>write</c> scope excludes secrets). Browser sessions are bound by role only.</summary>
    public static void RequireTokenScope(ICurrentActor actor)
    {
        if (actor.ApiTokenId is not null && !Scopes.Satisfies(actor.Scopes, Scopes.SecretsWrite))
            throw new ApiProblemException(ApiProblems.InsufficientScope(Scopes.SecretsWrite));
    }

    private static ApiProblem BindingForbidden(string detail) =>
        new(StatusCodes.Status403Forbidden, ResourceProblemCodes.SecretBindingForbidden, detail);

    // ---- SSH credential of a server -------------------------------------------------------------------------------------------------

    /// <summary>
    /// An Administrator points a server at a secret: the secret becomes the server's SSH credential (managed). It must be an organization-wide
    /// user secret (or already an SSH credential), and not be bound to an environment variable. Otherwise 422 at <paramref name="pointer"/>.
    /// </summary>
    public static async Task ClaimForServerAsync(AetheraDbContext db, Guid org, Guid secretId, string pointer, CancellationToken ct)
    {
        var secret = await db.SecretsOf(org).FirstOrDefaultAsync(s => s.Id == secretId, ct) ?? throw Lookups.BadReference(pointer, "secret");
        if (secret.Purpose == SecretPurpose.SshCredential) return;
        if (secret.IsManaged)
            throw Rejected(pointer, ResourceProblemCodes.SecretManaged, $"This secret is managed by another resource ({secret.Purpose.ToString().ToLowerInvariant()}).");
        if (secret.Scope != SecretScope.Organization)
            throw Rejected(pointer, "scope_mismatch", "An SSH credential must be an organization-wide secret.");
        if (await db.EnvironmentVariables.AnyAsync(v => v.SecretId == secretId, ct))
            throw Rejected(pointer, ResourceProblemCodes.SecretInUse, "This secret is bound to an environment variable and cannot become an SSH credential.");
        secret.Purpose = SecretPurpose.SshCredential;
    }

    /// <summary>A server no longer uses the secret: when no other server does, it is an ordinary (organization-wide) secret again.</summary>
    public static async Task ReleaseFromServerAsync(AetheraDbContext db, Guid secretId, Guid serverId, CancellationToken ct)
    {
        if (await db.Servers.AnyAsync(s => s.SshCredentialSecretId == secretId && s.Id != serverId, ct)) return;
        if (await db.Secrets.FirstOrDefaultAsync(s => s.Id == secretId, ct) is { Purpose: SecretPurpose.SshCredential } secret)
            secret.Purpose = SecretPurpose.User;
    }

    private static ApiProblemException Rejected(string pointer, string code, string message) =>
        new(ApiProblems.Validation([FieldError.AtPointer(pointer, code, message)]));
}
