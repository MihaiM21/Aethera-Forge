using System.Text.Json;
using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

public enum DeploymentRunResult { Running, Failed, Cancelled }

/// <summary>
/// Drives a <see cref="Deployment"/> through the nine-step pipeline (ADR 0004): Source, Build, Image, TargetServer, then the
/// strategy's Network/Container/Domain/HealthCheck steps, then Running. Transport and persistence are injected, so the runner
/// has no dependency on gRPC, SSH or EF Core.
/// </summary>
public sealed class DeploymentRunner(IServerTransport transport, IEnumerable<IDeploymentStrategy> strategies, IClock clock)
{
    private readonly Dictionary<string, IDeploymentStrategy> _strategies = strategies.ToDictionary(s => s.Name);

    /// <param name="deployment">A deployment already moved to InProgress (<see cref="Deployment.Start"/>).</param>
    /// <param name="save">Persists the deployment after each step.</param>
    public async Task<DeploymentRunResult> RunAsync(
        Deployment deployment, DeploymentPlan plan, Func<Deployment, CancellationToken, Task> save, CancellationToken ct)
    {
        Task Save(CancellationToken c) => save(deployment, c);
        try
        {
            var strategyName = plan.Compose is not null ? ComposeStrategy.StrategyName : deployment.Strategy;
            if (!_strategies.TryGetValue(strategyName, out var strategy))
                throw new DeploymentFailure(DeploymentStep.Source, FailureCodes.UnknownStrategy, $"Unknown deployment strategy '{deployment.Strategy}'.");

            var image = await PrepareImageAsync(deployment, plan, Save, ct);

            deployment.BeginStep(DeploymentStep.TargetServer, clock.UtcNow);
            var status = await transport.GetStatusAsync(plan.ServerId, ct);
            if (!status.Available)
                throw new DeploymentFailure(DeploymentStep.TargetServer, FailureCodes.ServerUnavailable, status.Reason ?? "The server is unavailable.");
            deployment.CompleteStep(DeploymentStep.TargetServer, clock.UtcNow);
            await Save(ct);

            var run = new DeploymentRun(deployment, plan, image, transport, clock, Save);
            deployment.ContainerIds = [.. await strategy.ExecuteAsync(run, ct)];

            deployment.ImageRef = image;
            deployment.MarkRunning(clock.UtcNow);
            await Save(CancellationToken.None);
            return DeploymentRunResult.Running;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            deployment.MarkCancelled(clock.UtcNow);
            await Save(CancellationToken.None);
            return DeploymentRunResult.Cancelled;
        }
        catch (DeploymentFailure f)
        {
            deployment.MarkFailed(f.Step, f.Code, f.Message, clock.UtcNow);
            await Save(CancellationToken.None);
            return DeploymentRunResult.Failed;
        }
    }

    private async Task<string> PrepareImageAsync(Deployment d, DeploymentPlan plan, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        var run = new DeploymentRun(d, plan, "", transport, clock, save);

        if (plan.Compose is not null)
        {
            d.SkipStep(DeploymentStep.Source, clock.UtcNow);
            d.SkipStep(DeploymentStep.Build, clock.UtcNow);
            d.SkipStep(DeploymentStep.Image, clock.UtcNow);
            return "";
        }

        if (plan.Build is { } spec)
        {
            d.BeginStep(DeploymentStep.Source, clock.UtcNow);
            d.CompleteStep(DeploymentStep.Source, clock.UtcNow);

            d.BeginStep(DeploymentStep.Build, clock.UtcNow);
            await save(ct);
            var built = await run.ExecuteAsync<BuildImageCommand, BuildOutcome>(
                new BuildImageCommand(spec), "build", DeploymentStep.Build, FailureCodes.BuildFailed, ct);
            d.CommitSha = built.CommitSha;
            d.ImageDigest = built.Digest;
            d.CompleteStep(DeploymentStep.Build, clock.UtcNow);

            d.BeginStep(DeploymentStep.Image, clock.UtcNow);
            var tag = built.Tags.FirstOrDefault() ?? spec.ImageTags[0];
            d.CompleteStep(DeploymentStep.Image, clock.UtcNow, JsonSerializer.Serialize(new { image = tag }));
            await save(ct);
            return tag;
        }

        var reference = plan.ImageReference
                        ?? throw new DeploymentFailure(DeploymentStep.Image, FailureCodes.SourceFailed, "The plan has neither a build nor an image reference.");
        d.SkipStep(DeploymentStep.Source, clock.UtcNow);
        d.SkipStep(DeploymentStep.Build, clock.UtcNow);
        d.BeginStep(DeploymentStep.Image, clock.UtcNow);
        if (plan.LocalImage)
        {
            try
            {
                await run.ExecuteAsync<ImageInspectCommand, DockerImage>(
                    new ImageInspectCommand(reference), "image.inspect", DeploymentStep.Image, FailureCodes.ImageMissing, ct);
            }
            catch (DeploymentFailure f) when (f.Code == FailureCodes.ImageMissing)
            {
                throw new DeploymentFailure(DeploymentStep.Image, FailureCodes.ImageMissing,
                    $"The image {reference} is no longer on the server (it was pruned); this deployment can no longer be rolled back to.", f);
            }
            d.CompleteStep(DeploymentStep.Image, clock.UtcNow);
            await save(ct);
            return reference;
        }
        var pulled = await run.ExecuteAsync<ImagePullCommand, ImagePulled>(
            new ImagePullCommand(reference, plan.PullAuth), "image.pull", DeploymentStep.Image, FailureCodes.ImagePullFailed, ct);
        d.ImageDigest = pulled.Digest;
        d.CompleteStep(DeploymentStep.Image, clock.UtcNow);
        await save(ct);
        return reference;
    }
}
