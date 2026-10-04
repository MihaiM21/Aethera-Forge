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

        d.SkipStep(DeploymentStep.Network, run.Clock.UtcNow);
        d.BeginStep(DeploymentStep.Container, run.Clock.UtcNow);
        var outcome = await run.ExecuteAsync<ComposeUpCommand, ComposeOutcome>(
            new ComposeUpCommand(project, PullPolicy: ComposePullPolicyKind.Missing, Build: false, RemoveOrphans: true, Wait: true, WaitTimeout: run.Plan.ComposeWaitTimeout),
            "compose.up", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
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
