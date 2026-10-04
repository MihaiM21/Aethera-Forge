using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

/// <summary>Services a strategy gets while it runs steps Network..HealthCheck of the pipeline.</summary>
public sealed class DeploymentRun(
    Deployment deployment, DeploymentPlan plan, string image, IServerTransport transport, IClock clock, Func<CancellationToken, Task> checkpoint)
{
    public Deployment Deployment { get; } = deployment;
    public DeploymentPlan Plan { get; } = plan;

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
