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
        d.CompleteStep(DeploymentStep.Container, run.Clock.UtcNow, run.ContainerStepDetails(created.ContainerId, newName));
        await run.SaveAsync(ct);

        d.SkipStep(DeploymentStep.Domain, run.Clock.UtcNow);

        try
        {
            await run.HealthCheckAsync(newName, ct);
        }
        catch
        {
            // Leave the old container serving; best effort cleanup of the candidate that never became healthy.
            await run.TryRemoveAsync(created.ContainerId, "container.remove_failed");
            if (plan.PreviousContainer is { Length: > 0 }) await run.NarrateAsync("The previous version keeps serving.");
            throw;
        }

        // The new container is healthy: the old one stops gracefully (it drains its requests), then goes.
        if (plan.PreviousContainer is { Length: > 0 } old)
            await run.StopAndRemoveAsync(old, "container.remove_old", ct);

        await run.SaveAsync(ct);
        return [created.ContainerId];
    }
}
