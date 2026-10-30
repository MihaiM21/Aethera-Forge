using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

/// <summary>Services a strategy gets while it runs steps Network..HealthCheck of the pipeline.</summary>
public sealed class DeploymentRun(
    Deployment deployment, DeploymentPlan plan, string image, IServerTransport transport, IClock clock, Func<CancellationToken, Task> checkpoint,
    string? strategyNote = null)
{
    public Deployment Deployment { get; } = deployment;
    public DeploymentPlan Plan { get; } = plan;

    /// <summary>Why the strategy that runs is not the one the workload asked for (<see cref="DeploymentRunner.ChooseStrategy"/>); recorded on the Container step.</summary>
    public string? StrategyNote { get; } = strategyNote;

    /// <summary>The details recorded when the Container step succeeds: the container, and the strategy note when there is one.</summary>
    public string ContainerStepDetails(string containerId, string? name = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { containerId, name, strategyNote = StrategyNote },
            new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    /// <summary>
    /// Stops <paramref name="container"/> gracefully (SIGTERM, then SIGKILL after <see cref="DeploymentPlan.StopTimeout"/>) and removes it. A stop that fails
    /// for any reason but an unreachable server (it is not running, it is already gone) falls back to a forced removal; the removal is idempotent.
    /// </summary>
    public async Task StopAndRemoveAsync(string container, string operation, CancellationToken ct)
    {
        var stopped = true;
        try
        {
            await ExecuteAsync<ContainerStopCommand, DockerContainer>(
                new ContainerStopCommand(container, Plan.StopTimeout), $"{operation}.stop", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
        }
        catch (DeploymentFailure f) when (f.Code != FailureCodes.ServerUnavailable && !ct.IsCancellationRequested)
        {
            stopped = false;
        }

        await ExecuteAsync<ContainerRemoveCommand, Unit>(
            new ContainerRemoveCommand(container, Force: !stopped), operation, DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
    }

    /// <summary>Lines of a failed container's output copied into the deployment's log.</summary>
    public const int FailureLogLines = 100;

    /// <summary>
    /// Runs the HealthCheck step against <paramref name="container"/> (skipped when the plan has no probe). A failure copies the container's last
    /// lines of output into the deployment's log before it is thrown: why it did not come up is usually there.
    /// </summary>
    public async Task HealthCheckAsync(string container, CancellationToken ct)
    {
        var target = Plan.HealthFor?.Invoke(container) ?? Plan.Health;
        if (target is null)
        {
            Deployment.SkipStep(DeploymentStep.HealthCheck, Clock.UtcNow);
            return;
        }

        Deployment.BeginStep(DeploymentStep.HealthCheck, Clock.UtcNow);
        try
        {
            var probe = await ExecuteAsync<HealthProbeCommand, HealthProbeOutcome>(
                new HealthProbeCommand(target, Plan.HealthTimeout, Plan.HealthInterval, Plan.HealthRetries, Plan.HealthStartPeriod, Plan.HealthSuccessThreshold),
                "health", DeploymentStep.HealthCheck, FailureCodes.HealthTimeout, ct);
            Deployment.HealthCheckResultJson = System.Text.Json.JsonSerializer.Serialize(new { probe.Healthy, probe.Attempts, probe.HttpStatus, probe.Detail });
            if (!probe.Healthy)
                throw new DeploymentFailure(DeploymentStep.HealthCheck, FailureCodes.HealthTimeout,
                    $"Health check failed after {probe.Attempts} attempts: {probe.Detail}");
        }
        catch (DeploymentFailure f) when (f.Code != FailureCodes.ServerUnavailable)
        {
            await CopyContainerLogsAsync(container);
            throw;
        }

        Deployment.CompleteStep(DeploymentStep.HealthCheck, Clock.UtcNow);
    }

    /// <summary>Best effort: the last <see cref="FailureLogLines"/> lines of the container's output, into the deployment's log.</summary>
    public async Task CopyContainerLogsAsync(string container)
    {
        if (Plan.WriteLog is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var any = false;
            await foreach (var entry in transport.StreamLogsAsync(Plan.ServerId, new LogStreamRequest(container, Follow: false, Since: null, Tail: FailureLogLines), cts.Token))
            {
                if (!any) await NarrateAsync($"The last {FailureLogLines} lines of the container's output:");
                any = true;
                if (entry.Text.Length > 0) await Plan.WriteLog(entry.Stream, entry.Text.TrimEnd('\n'), cts.Token);
            }

            if (!any) await NarrateAsync("The container wrote no output.");
        }
        catch (Exception e) when (e is ServerTransportException or OperationCanceledException or InvalidOperationException or NotSupportedException)
        {
            await NarrateAsync("The container's output could not be read.");
        }
    }

    /// <summary>Control-plane narration in the deployment's log; never fails the deployment.</summary>
    public async Task NarrateAsync(string line)
    {
        if (Plan.Narrate is null) return;
        try
        {
            await Plan.Narrate(line, CancellationToken.None);
        }
        catch (Exception)
        {
            // Logging is a convenience here.
        }
    }

    /// <summary>Removes a container that did not make it, ignoring every error: the deployment already failed for a more useful reason.</summary>
    public async Task TryRemoveAsync(string container, string operation)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ExecuteAsync<ContainerRemoveCommand, Unit>(
                new ContainerRemoveCommand(container, Force: true), operation, DeploymentStep.Container, FailureCodes.ContainerFailed, cts.Token);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>The image to run: the build's tag or the pulled reference.</summary>
    public string Image { get; } = image;

    public IClock Clock { get; } = clock;

    /// <summary>Persists the deployment (step rows are the resume checkpoint).</summary>
    public Task SaveAsync(CancellationToken ct) => checkpoint(ct);

    /// <summary>
    /// Runs one typed command; transport failures and failed commands become a <see cref="DeploymentFailure"/> at <paramref name="step"/>.
    /// <paramref name="serverId"/> is the server it runs on: the plan's server unless the build runs elsewhere (ADR 0012).
    /// </summary>
    public async Task<TResult> ExecuteAsync<TCommand, TResult>(
        TCommand command, string operation, DeploymentStep step, string failureCode, CancellationToken ct, Guid? serverId = null)
        where TCommand : IServerCommand<TResult>
    {
        var options = new CommandOptions
        {
            IdempotencyKey = $"{Plan.JobId?.ToString() ?? Deployment.Id.ToString()}:{operation}:0",
            JobId = Plan.JobId,
            OrganizationId = Plan.OrganizationId,
            OnLog = Plan.OnLog,
            LogStreamId = $"deploy:{Deployment.Id}",
        };
        try
        {
            var outcome = await transport.ExecuteAsync<TCommand, TResult>(serverId ?? Plan.ServerId, command, options, ct);
            return outcome.EnsureSucceeded();
        }
        catch (ServerTransportException e)
        {
            throw new DeploymentFailure(step, e.Transient ? FailureCodes.ServerUnavailable : failureCode, e.Message, e);
        }
    }
}

/// <summary>
/// Runs steps Network, Container, Domain and HealthCheck for one deployment (ADR 0004). Implementations record each step on the
/// <see cref="Deployment"/> and throw <see cref="DeploymentFailure"/>; the runner turns that into a failed deployment.
/// </summary>
public interface IDeploymentStrategy
{
    /// <summary>One of <see cref="DeploymentStrategies"/>.</summary>
    string Name { get; }

    /// <summary>Returns the container ids of the new deployment.</summary>
    Task<IReadOnlyList<string>> ExecuteAsync(DeploymentRun run, CancellationToken cancellationToken);
}
