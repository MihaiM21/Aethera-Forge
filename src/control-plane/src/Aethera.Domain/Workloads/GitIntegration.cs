namespace Aethera.Domain;

public enum GitCredentialKind
{
    Token = 0,
    DeployKey = 1,
    BasicAuth = 2,
}

/// <summary>Reusable credential for cloning private repositories; the secret material is a <see cref="Secret"/>.</summary>
public class GitCredential : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }
    public GitCredentialKind Kind { get; set; } = GitCredentialKind.Token;
    public GitProvider Provider { get; set; } = GitProvider.Generic;
    public string? Username { get; set; }

    /// <summary>Public half of a deploy key, shown to the user so they can register it with the provider.</summary>
    public string? PublicKey { get; set; }

    public Guid SecretId { get; set; }
    public Secret Secret { get; set; } = null!;
}

/// <summary>Inbound Git webhook for an application (<c>POST /webhooks/git/{endpointId}</c>); the endpoint id is this entity's id.</summary>
public class WebhookEndpoint : MutableEntity
{
    public Guid WorkloadId { get; set; }
    public Workload Workload { get; set; } = null!;
    public GitProvider Provider { get; set; } = GitProvider.GitHub;

    /// <summary>Shared secret / token used to verify signatures (GitHub HMAC, GitLab token).</summary>
    public Guid SecretId { get; set; }
    public Secret Secret { get; set; } = null!;

    public bool Enabled { get; set; } = true;

    /// <summary>Optional glob of branches that trigger a deployment; null = the application's configured branch.</summary>
    public string? BranchFilter { get; set; }

    public DateTimeOffset? LastDeliveryAt { get; set; }
}

public enum WebhookOutcome
{
    Accepted = 0,
    Ignored = 1,
    Rejected = 2,
    Duplicate = 3,
}

/// <summary>Immutable record of one webhook delivery. (endpoint, provider delivery id) is the idempotency key.</summary>
public class WebhookDelivery : Entity
{
    public Guid EndpointId { get; init; }
    public WebhookEndpoint Endpoint { get; set; } = null!;
    public required string DeliveryId { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? EventType { get; init; }
    public string? Ref { get; init; }
    public string? CommitSha { get; init; }
    public bool SignatureValid { get; init; }
    public WebhookOutcome Outcome { get; init; }
    public string? Detail { get; init; }
    public Guid? JobId { get; init; }
    public Guid? DeploymentId { get; init; }
}
