using System.Text.Json;
using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

/// <summary>Stops and removes the old container, then starts the new one (brief downtime, the MVP default).</summary>
public sealed class RecreateStrategy : IDeploymentStrategy
{
    public string Name => DeploymentStrategies.Recreate;

    public async Task<IReadOnlyList<string>> ExecuteAsync(DeploymentRun run, CancellationToken ct)
    {
        var d = run.Deployment;
        var plan = run.Plan;

        d.BeginStep(DeploymentStep.Network, run.Clock.UtcNow);
        foreach (var network in plan.Networks)
            await run.ExecuteAsync<NetworkCreateCommand, DockerNetwork>(
                new NetworkCreateCommand(network, Internal: plan.InternalNetworks.Contains(network), IfNotExists: true), $"network.{network}", DeploymentStep.Network, FailureCodes.NetworkFailed, ct);
        d.CompleteStep(DeploymentStep.Network, run.Clock.UtcNow);
        await run.SaveAsync(ct);

        d.BeginStep(DeploymentStep.Container, run.Clock.UtcNow);
        if (plan.PreviousContainer is { Length: > 0 } old)
            await run.ExecuteAsync<ContainerRemoveCommand, Unit>(
                new ContainerRemoveCommand(old, Force: true), "container.remove_old", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);

        var created = await run.ExecuteAsync<ContainerCreateCommand, ContainerCreated>(
            new ContainerCreateCommand(plan.Container with { Image = run.Image }, Start: true, ReplaceExisting: true),
            "container.create", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
        d.CompleteStep(DeploymentStep.Container, run.Clock.UtcNow, JsonSerializer.Serialize(new { containerId = created.ContainerId }));
        await run.SaveAsync(ct);

        // Domains and routing are attached through container labels by the proxy provider (WP3.3).
        d.SkipStep(DeploymentStep.Domain, run.Clock.UtcNow);

        var target = plan.HealthFor?.Invoke(plan.Container.Name) ?? plan.Health;
        if (target is null)
        {
            d.SkipStep(DeploymentStep.HealthCheck, run.Clock.UtcNow);
        }
        else
        {
            d.BeginStep(DeploymentStep.HealthCheck, run.Clock.UtcNow);
            var probe = await run.ExecuteAsync<HealthProbeCommand, HealthProbeOutcome>(
                new HealthProbeCommand(target, plan.HealthTimeout, plan.HealthInterval, plan.HealthRetries, plan.HealthStartPeriod),
                "health", DeploymentStep.HealthCheck, FailureCodes.HealthTimeout, ct);
            d.HealthCheckResultJson = JsonSerializer.Serialize(new { probe.Healthy, probe.Attempts, probe.HttpStatus, probe.Detail });
            if (!probe.Healthy)
                throw new DeploymentFailure(DeploymentStep.HealthCheck, FailureCodes.HealthTimeout,
                    $"Health check failed after {probe.Attempts} attempts: {probe.Detail}");
            d.CompleteStep(DeploymentStep.HealthCheck, run.Clock.UtcNow);
        }

        await run.SaveAsync(ct);
        return [created.ContainerId];
    }
}
