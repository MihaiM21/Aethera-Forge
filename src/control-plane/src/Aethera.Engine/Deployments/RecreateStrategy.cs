using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

/// <summary>
/// Stops the old container, then starts the new one (brief downtime): for workloads that publish a host port or write to a volume, which two
/// containers cannot share. The old container is only stopped, not removed, until the new one is healthy: when the new one does not start or
/// fails its health check it is removed and the old one is started again, so a broken deployment never leaves the application down.
/// </summary>
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
        // The old container gets its grace period (SIGTERM): a database flushes, a server drains its requests. It frees its host port and its
        // volumes but stays, so it can be started again.
        var old = plan.PreviousContainer is { Length: > 0 } previous ? previous : null;
        if (old is not null) await StopOldAsync(run, old, ct);

        // A distinct name per deployment, so the stopped old container keeps its own.
        var newName = $"{plan.Container.Name}-{d.Number}";
        ContainerCreated created;
        try
        {
            created = await run.ExecuteAsync<ContainerCreateCommand, ContainerCreated>(
                new ContainerCreateCommand(plan.Container with { Image = run.Image, Name = newName }, Start: true, ReplaceExisting: true),
                "container.create", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
        }
        catch (DeploymentFailure f) when (old is not null && f.Code != FailureCodes.ServerUnavailable)
        {
            await run.TryRemoveAsync(newName, "container.remove_failed");
            throw await RestoreAsync(run, old, f);
        }

        d.CompleteStep(DeploymentStep.Container, run.Clock.UtcNow, run.ContainerStepDetails(created.ContainerId, newName));
        await run.SaveAsync(ct);

        // Domains and routing are attached through container labels by the proxy provider (WP3.3).
        d.SkipStep(DeploymentStep.Domain, run.Clock.UtcNow);

        try
        {
            await run.HealthCheckAsync(newName, ct);
        }
        catch (DeploymentFailure f) when (old is not null && f.Code != FailureCodes.ServerUnavailable)
        {
            await run.TryRemoveAsync(created.ContainerId, "container.remove_failed");
            throw await RestoreAsync(run, old, f);
        }

        if (old is not null)
            await run.ExecuteAsync<ContainerRemoveCommand, Unit>(
                new ContainerRemoveCommand(old, Force: true), "container.remove_old", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);

        await run.SaveAsync(ct);
        return [created.ContainerId];
    }

    private static async Task StopOldAsync(DeploymentRun run, string old, CancellationToken ct)
    {
        try
        {
            await run.ExecuteAsync<ContainerStopCommand, DockerContainer>(
                new ContainerStopCommand(old, run.Plan.StopTimeout), "container.stop_old", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
        }
        catch (DeploymentFailure f) when (f.Code != FailureCodes.ServerUnavailable && !ct.IsCancellationRequested)
        {
            // Already stopped or gone: nothing holds its port.
        }
    }

    /// <summary>Starts the old container again and returns the failure to throw, saying whether the previous version is back.</summary>
    private static async Task<DeploymentFailure> RestoreAsync(DeploymentRun run, string old, DeploymentFailure failure)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await run.ExecuteAsync<ContainerStartCommand, DockerContainer>(
                new ContainerStartCommand(old), "container.restore_old", DeploymentStep.Container, FailureCodes.ContainerFailed, cts.Token);
            run.Plan.PreviousRestored = true;
            await run.NarrateAsync("The previous version was started again; it keeps serving.");
            return new DeploymentFailure(failure.Step, failure.Code, $"{failure.Message} The previous version was started again.", failure);
        }
        catch (Exception e) when (e is DeploymentFailure or OperationCanceledException)
        {
            await run.NarrateAsync("The previous version could not be started again.");
            return failure;
        }
    }
}
