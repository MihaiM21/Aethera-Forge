using System.Text.Json;
using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

/// <summary>
/// Start the new container next to the old one, wait until it is healthy, then remove the old one (ADR 0004). Both containers carry the
/// same proxy labels, so the proxy adds the new one to the route as soon as it is up and the old one drops out when it is removed.
/// A failed health check removes only the new container; the old one keeps serving.
/// </summary>
public sealed class LowDowntimeStrategy : IDeploymentStrategy
{
    public string Name => DeploymentStrategies.LowDowntime;

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

        // A distinct name per deployment lets old and new coexist; the rollback target keeps its own name too.
        var newName = $"{plan.Container.Name}-{d.Number}";
        d.BeginStep(DeploymentStep.Container, run.Clock.UtcNow);
        var created = await run.ExecuteAsync<ContainerCreateCommand, ContainerCreated>(
            new ContainerCreateCommand(plan.Container with { Image = run.Image, Name = newName }, Start: true, ReplaceExisting: true),
            "container.create", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
        d.CompleteStep(DeploymentStep.Container, run.Clock.UtcNow, JsonSerializer.Serialize(new { containerId = created.ContainerId, name = newName }));
        await run.SaveAsync(ct);

        d.SkipStep(DeploymentStep.Domain, run.Clock.UtcNow);

        var target = plan.HealthFor?.Invoke(newName) ?? plan.Health;
        try
        {
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
        }
        catch
        {
            // Leave the old container serving; best effort cleanup of the candidate that never became healthy.
            await TryRemoveAsync(run, created.ContainerId);
            throw;
        }

        if (plan.PreviousContainer is { Length: > 0 } old)
            await run.ExecuteAsync<ContainerRemoveCommand, Unit>(
                new ContainerRemoveCommand(old, Force: true), "container.remove_old", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);

        await run.SaveAsync(ct);
        return [created.ContainerId];
    }

    private static async Task TryRemoveAsync(DeploymentRun run, string container)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await run.ExecuteAsync<ContainerRemoveCommand, Unit>(
                new ContainerRemoveCommand(container, Force: true), "container.remove_failed", DeploymentStep.Container, FailureCodes.ContainerFailed, cts.Token);
        }
        catch (Exception)
        {
            // The deployment already failed for a more useful reason.
        }
    }
}
