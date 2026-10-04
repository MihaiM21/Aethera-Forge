namespace Aethera.Engine.Deployments;

/// <summary>Image naming and the cleanup policy for old deployment images (ADR 0004).</summary>
public static class ImagePolicy
{
    /// <summary>Spec section 15: <c>aethera/&lt;app&gt;:&lt;deploymentId&gt;</c>.</summary>
    public static string Tag(string applicationSlug, Guid deploymentId) => $"aethera/{applicationSlug}:{deploymentId:D}";

    /// <summary>One image held for an application, newest first by <see cref="CreatedAt"/>.</summary>
    public sealed record Candidate(string Image, Guid DeploymentId, DateTimeOffset CreatedAt, bool IsRollbackPoint);

    /// <summary>
    /// Images to delete: everything except the live deployment's image and the newest <paramref name="keepRollbackPoints"/> rollback
    /// points. Images of failed or cancelled deployments are never rollback points and are removed.
    /// </summary>
    public static IReadOnlyList<string> ImagesToRemove(IEnumerable<Candidate> images, Guid? currentDeploymentId, int keepRollbackPoints)
    {
        var all = images.OrderByDescending(i => i.CreatedAt).ToList();
        var keep = all.Where(i => i.DeploymentId == currentDeploymentId).Select(i => i.Image).ToHashSet();
        foreach (var i in all.Where(i => i.IsRollbackPoint).Take(Math.Max(0, keepRollbackPoints)))
            keep.Add(i.Image);
        return all.Where(i => !keep.Contains(i.Image)).Select(i => i.Image).Distinct().ToList();
    }
}
