namespace Aethera.Domain;

/// <summary>
/// Append-only audit trail (spec section 32). The actor, request id and IP come from <see cref="ICurrentActor"/>; callers
/// only describe what happened.
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Records one event.
    /// </summary>
    /// <param name="action">Dotted verb, <c>resource.verb</c>: <c>application.created</c>, <c>api_token.revoked</c>, <c>auth.login_failed</c>.</param>
    /// <param name="resourceType">The kind of resource acted on (<c>application</c>, <c>secret</c>), or the empty string if none.</param>
    /// <param name="resourceId">The affected resource, if any.</param>
    /// <param name="metadata">
    /// Extra context: any object that serializes to a JSON object (an anonymous type is fine). Keys that look sensitive
    /// (<c>password|secret|token|key|value</c>, case-insensitive, any depth) are redacted before storing. Never pass request
    /// bodies or secret values here regardless; redaction is a safety net, not permission.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// The default implementation adds the event to the scoped database context and saves it, so call it after your own
    /// <c>SaveChangesAsync</c> (or before it, to commit both atomically).
    /// </remarks>
    Task RecordAsync(
        string action,
        string resourceType,
        Guid? resourceId,
        object? metadata,
        CancellationToken cancellationToken = default);
}
