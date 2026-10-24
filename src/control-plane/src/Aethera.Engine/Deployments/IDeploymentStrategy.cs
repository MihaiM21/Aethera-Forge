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

    /// <summary>The image to run: the build's tag or the pulled reference.</summary>
    public string Image { get; } = image;

    public IClock Clock { get; } = clock;

    /// <summary>Persists the deployment (step rows are the resume checkpoint).</summary>
    public Task SaveAsync(CancellationToken ct) => checkpoint(ct);

    /// <summary>Runs one typed command; transport failures and failed commands become a <see cref="DeploymentFailure"/> at <paramref name="step"/>.</summary>
    public async Task<TResult> ExecuteAsync<TCommand, TResult>(
        TCommand command, string operation, DeploymentStep step, string failureCode, CancellationToken ct)
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
            var outcome = await transport.ExecuteAsync<TCommand, TResult>(Plan.ServerId, command, options, ct);
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
