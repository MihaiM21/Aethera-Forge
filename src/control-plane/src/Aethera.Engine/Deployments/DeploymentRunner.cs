using System.Text.Json;
using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

/// <summary><see cref="Retry"/>: a transient failure (the server could not be reached) that the job will retry; the deployment stays in progress.</summary>
public enum DeploymentRunResult { Running, Failed, Cancelled, Retry }

/// <summary>
/// Drives a <see cref="Deployment"/> through the nine-step pipeline (ADR 0004): Source, Build, Image, TargetServer, then the
/// strategy's Network/Container/Domain/HealthCheck steps, then Running. Transport and persistence are injected, so the runner
/// has no dependency on gRPC, SSH or EF Core.
/// </summary>
public sealed class DeploymentRunner(IServerTransport transport, IEnumerable<IDeploymentStrategy> strategies, IClock clock)
{
    private readonly Dictionary<string, IDeploymentStrategy> _strategies = strategies.ToDictionary(s => s.Name);

    /// <summary>The failure behind a <see cref="DeploymentRunResult.Retry"/>.</summary>
    public DeploymentFailure? TransientFailure { get; private set; }

    /// <summary>
    /// The strategy that runs <paramref name="plan"/> and, when it is not the requested one, why. Low-downtime starts the new container beside the
    /// old one, which a published host port forbids (the port is taken) and a writable volume makes dangerous (two processes on one data directory
    /// corrupt it): such a workload is recreated instead.
    /// </summary>
    public static (string Name, string? Note) ChooseStrategy(string requested, DeploymentPlan plan)
    {
        if (plan.Compose is not null) return (ComposeStrategy.StrategyName, null);
        if (requested != DeploymentStrategies.LowDowntime) return (requested, null);

        var reasons = new List<string>();
        var ports = plan.Container.Ports?.Select(p => p.HostPort).Where(p => p > 0).Distinct().Order().ToList() ?? [];
        if (ports.Count > 0) reasons.Add($"publishes host port {string.Join(", ", ports)}");
        var writable = plan.Container.Mounts?.Where(m => m.Type is MountKind.Volume or MountKind.Bind && !m.ReadOnly).Select(m => m.Target).Distinct().Order().ToList() ?? [];
        if (writable.Count > 0) reasons.Add($"has a writable volume ({string.Join(", ", writable)})");
        return reasons.Count == 0
            ? (requested, null)
            : (DeploymentStrategies.Recreate,
                $"Low-downtime needs two containers side by side, but this workload {string.Join(" and ", reasons)}: two processes cannot share them. The recreate strategy was used.");
    }

    /// <param name="deployment">A deployment already moved to InProgress (<see cref="Deployment.Start"/>).</param>
    /// <param name="save">Persists the deployment after each step.</param>
    /// <param name="finalAttempt">
    /// False while the job will be retried: a transient failure (<see cref="FailureCodes.ServerUnavailable"/>) then leaves the deployment in progress
    /// and returns <see cref="DeploymentRunResult.Retry"/>, so the next attempt redeploys from the frozen snapshot instead of finding a finished one.
    /// </param>
    public async Task<DeploymentRunResult> RunAsync(
        Deployment deployment, DeploymentPlan plan, Func<Deployment, CancellationToken, Task> save, CancellationToken ct, bool finalAttempt = true)
    {
        Task Save(CancellationToken c) => save(deployment, c);
        TransientFailure = null;
        try
        {
            var (strategyName, strategyNote) = ChooseStrategy(deployment.Strategy, plan);
            if (!_strategies.TryGetValue(strategyName, out var strategy))
                throw new DeploymentFailure(DeploymentStep.Source, FailureCodes.UnknownStrategy, $"Unknown deployment strategy '{deployment.Strategy}'.");

            var image = await PrepareImageAsync(deployment, plan, Save, ct);

            deployment.BeginStep(DeploymentStep.TargetServer, clock.UtcNow);
            var status = await transport.GetStatusAsync(plan.ServerId, ct);
            if (!status.Available)
                throw new DeploymentFailure(DeploymentStep.TargetServer, FailureCodes.ServerUnavailable, status.Reason ?? "The server is unavailable.");
            deployment.CompleteStep(DeploymentStep.TargetServer, clock.UtcNow);
            await Save(ct);

            var run = new DeploymentRun(deployment, plan, image, transport, clock, Save, strategyNote);
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
        catch (DeploymentFailure f) when (!finalAttempt && f.Code == FailureCodes.ServerUnavailable)
        {
            TransientFailure = f;
            await Save(CancellationToken.None);
            return DeploymentRunResult.Retry;
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
            plan.BuildResult = built;
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

        // The pull policy: Always pulls, IfNotPresent pulls only an image the server does not have, Never never pulls (and says so when it is missing).
        if (plan.PullPolicy != ImagePullPolicy.Always)
        {
            DockerImage? present = null;
            try
            {
                present = await run.ExecuteAsync<ImageInspectCommand, DockerImage>(
                    new ImageInspectCommand(reference), "image.inspect", DeploymentStep.Image, FailureCodes.ImageMissing, ct);
            }
            catch (DeploymentFailure f) when (f.Code == FailureCodes.ImageMissing)
            {
                // Not on the server (or not inspectable): pulled below unless the policy forbids it.
            }

            if (present is not null)
            {
                var repoDigest = present.RepoDigests.FirstOrDefault() ?? "";
                d.ImageDigest = repoDigest.Contains('@') ? repoDigest[(repoDigest.IndexOf('@') + 1)..] : present.Id;
                d.CompleteStep(DeploymentStep.Image, clock.UtcNow, JsonSerializer.Serialize(new { image = reference, pulled = false, policy = plan.PullPolicy.ToString() }));
                await save(ct);
                return reference;
            }

            if (plan.PullPolicy == ImagePullPolicy.Never)
                throw new DeploymentFailure(DeploymentStep.Image, FailureCodes.ImageMissing,
                    $"The image {reference} is not on the server and its pull policy is Never. Pull it there yourself or change the pull policy.");
        }

        var pulled = await run.ExecuteAsync<ImagePullCommand, ImagePulled>(
            new ImagePullCommand(reference, plan.PullAuth), "image.pull", DeploymentStep.Image, FailureCodes.ImagePullFailed, ct);
        d.ImageDigest = pulled.Digest;
        d.CompleteStep(DeploymentStep.Image, clock.UtcNow);
        await save(ct);
        return reference;
    }
}
