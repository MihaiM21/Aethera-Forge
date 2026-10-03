namespace Aethera.Domain;

public enum IdempotencyState
{
    InProgress = 0,
    Completed = 1,
}

/// <summary>
/// Stored <c>Idempotency-Key</c> (ADR 0003): (principal, key, method, path, SHA-256(body)) and, when finished, the response.
/// Kept 24 hours. Keys are scoped to the authenticated principal (user or API token id).
/// </summary>
public class IdempotencyRecord
{
    public Guid PrincipalId { get; set; }
    public required string Key { get; set; }
    public required string Method { get; set; }
    public required string Path { get; set; }
    public required byte[] RequestHash { get; set; }
    public IdempotencyState State { get; set; } = IdempotencyState.InProgress;
    public int? ResponseStatus { get; set; }
    public string? ResponseHeadersJson { get; set; }
    public byte[]? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Instance configuration (<c>/settings</c>): one jsonb value per key.</summary>
public class InstanceSetting
{
    public required string Key { get; set; }
    public string ValueJson { get; set; } = "null";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
