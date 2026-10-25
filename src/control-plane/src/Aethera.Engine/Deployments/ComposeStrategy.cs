using System.Text.Json;
using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

/// <summary>
/// Runs a compose project (project-scoped; the user's file is kept as is, Aethera adds an override file for labels and networks).
/// <c>compose up --wait</c> does the starting and health waiting; afterwards every service must be running (and not unhealthy).
/// </summary>
public sealed class ComposeStrategy : IDeploymentStrategy
{
    public const string StrategyName = "compose";

    public string Name => StrategyName;

    public async Task<IReadOnlyList<string>> ExecuteAsync(DeploymentRun run, CancellationToken ct)
    {
        var d = run.Deployment;
        var project = run.Plan.Compose ?? throw new DeploymentFailure(DeploymentStep.Container, FailureCodes.SourceFailed, "The plan has no compose project.");

        // The override joins every service to the environment network (and the proxy network, and custom ones) as external networks: they must exist.
        d.BeginStep(DeploymentStep.Network, run.Clock.UtcNow);
        foreach (var network in run.Plan.Networks)
            await run.ExecuteAsync<NetworkCreateCommand, DockerNetwork>(
                new NetworkCreateCommand(network, Internal: run.Plan.InternalNetworks.Contains(network), IfNotExists: true), $"network.{network}", DeploymentStep.Network, FailureCodes.NetworkFailed, ct);
        d.CompleteStep(DeploymentStep.Network, run.Clock.UtcNow);
        await run.SaveAsync(ct);

        d.BeginStep(DeploymentStep.Container, run.Clock.UtcNow);
        // A compose file from the repository is built on the server (`build:` services); an inline file has no build context.
        var outcome = await run.ExecuteAsync<ComposeUpCommand, ComposeOutcome>(
            new ComposeUpCommand(project, PullPolicy: ComposePullPolicyKind.Missing, Build: project.Git is not null, RemoveOrphans: true, Wait: true, WaitTimeout: run.Plan.ComposeWaitTimeout),
            "compose.up", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
        if (outcome.CommitSha is { Length: > 0 } commit) d.CommitSha = commit;
        d.CompleteStep(DeploymentStep.Container, run.Clock.UtcNow,
            JsonSerializer.Serialize(new { services = outcome.Services.Select(s => new { s.Service, state = s.State.ToString(), health = s.Health.ToString() }) }));
        await run.SaveAsync(ct);

        d.SkipStep(DeploymentStep.Domain, run.Clock.UtcNow);

        d.BeginStep(DeploymentStep.HealthCheck, run.Clock.UtcNow);
        var bad = outcome.Services.Where(s => s.State != ContainerRunState.Running || s.Health == ContainerHealthState.Unhealthy).ToList();
        d.HealthCheckResultJson = JsonSerializer.Serialize(new { Healthy = bad.Count == 0, Services = outcome.Services.Count });
        if (outcome.Services.Count == 0)
            throw new DeploymentFailure(DeploymentStep.HealthCheck, FailureCodes.HealthTimeout, "The compose project started no services.");
        if (bad.Count > 0)
            throw new DeploymentFailure(DeploymentStep.HealthCheck, FailureCodes.HealthTimeout,
                "Services not running or unhealthy: " + string.Join(", ", bad.Select(s => $"{s.Service} ({s.State}/{s.Health})")));
        d.CompleteStep(DeploymentStep.HealthCheck, run.Clock.UtcNow);
        await run.SaveAsync(ct);

        return outcome.Services.Select(s => s.ContainerId).ToList();
    }
}
