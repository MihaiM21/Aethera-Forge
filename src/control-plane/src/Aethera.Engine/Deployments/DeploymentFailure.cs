using Aethera.Domain;

namespace Aethera.Engine.Deployments;

/// <summary>A pipeline step failed. <see cref="Code"/> is the machine-readable failure code stored on the deployment (ADR 0004).</summary>
public sealed class DeploymentFailure(DeploymentStep step, string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public DeploymentStep Step { get; } = step;
    public string Code { get; } = code;
}

public static class FailureCodes
{
    public const string SourceFailed = "source.failed";
    public const string BuildFailed = "build.failed";
    public const string ImagePullFailed = "image.pull_failed";
    public const string ServerUnavailable = "server.unavailable";
    public const string ContainerFailed = "container.failed";
    public const string NetworkFailed = "network.failed";

    /// <summary>A port is published on the private network of a server that has no mesh address (ADR 0012).</summary>
    public const string NoMeshAddress = "network.no_mesh_address";
    public const string HealthTimeout = "health.timeout";
    public const string ImageMissing = "image.missing";
    public const string UnknownStrategy = "strategy.unknown";
    public const string InternalError = "internal.error";
}
