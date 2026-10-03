namespace Aethera.Domain;

public enum AuditActorType
{
    System = 0,
    User = 1,
    ApiToken = 2,
    Agent = 3,
}

/// <summary>
/// Immutable audit record (spec §32). Actor ids are intentionally not foreign keys so history survives
/// user/token deletion; <see cref="ActorLabel"/> snapshots a readable name.
/// </summary>
public class AuditEvent : Entity
{
    public Guid OrganizationId { get; init; }
    public AuditActorType ActorType { get; init; }
    public Guid? ActorUserId { get; init; }
    public Guid? ActorApiTokenId { get; init; }
    public string? ActorLabel { get; init; }

    /// <summary>Dotted verb, e.g. <c>deployment.created</c>, <c>env_var.updated</c>, <c>api_token.created</c>.</summary>
    public required string Action { get; init; }
    public string? ResourceType { get; init; }
    public Guid? ResourceId { get; init; }
    public string? ResourceName { get; init; }

    /// <summary>Extra context (jsonb). Redacted; must never contain secret values or request bodies.</summary>
    public string MetadataJson { get; init; } = "{}";
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>Correlation id of the request (<c>X-Request-Id</c> / trace id).</summary>
    public string? RequestId { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Append-only timeline of state changes and lifecycle actions of a resource (server or workload): status axis
/// transitions (debounced by the caller) that later feed alerts, and events like <c>restarted</c> that have no deployment record.
/// </summary>
public class ResourceEvent : Entity
{
    public required string ResourceType { get; init; }
    public Guid ResourceId { get; init; }

    /// <summary>e.g. <c>status.changed</c>, <c>restarted</c>, <c>stopped</c>, <c>started</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Status axis for <c>status.changed</c>: <c>reachability</c>, <c>agent</c>, <c>docker</c>, <c>application</c>.</summary>
    public string? Axis { get; init; }

    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string? Detail { get; init; }
    public Guid? ActorUserId { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
