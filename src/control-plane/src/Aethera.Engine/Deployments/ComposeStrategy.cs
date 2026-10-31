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

        // The override joins every service to the environment network (and the proxy network, and custom ones) as external networks: they must exist.
        d.BeginStep(DeploymentStep.Network, run.Clock.UtcNow);
        foreach (var network in run.Plan.Networks)
            await run.ExecuteAsync<NetworkCreateCommand, DockerNetwork>(
                new NetworkCreateCommand(network, Internal: run.Plan.InternalNetworks.Contains(network), IfNotExists: true), $"network.{network}", DeploymentStep.Network, FailureCodes.NetworkFailed, ct);
        d.CompleteStep(DeploymentStep.Network, run.Clock.UtcNow);
        await run.SaveAsync(ct);

        d.BeginStep(DeploymentStep.Container, run.Clock.UtcNow);
        // A compose file from the repository is built on the server (`build:` services); an inline file has no build context.
        ComposeOutcome outcome;
        try
        {
            outcome = await run.ExecuteAsync<ComposeUpCommand, ComposeOutcome>(
                new ComposeUpCommand(project, PullPolicy: ComposePullPolicyKind.Missing, Build: project.Git is not null, RemoveOrphans: true, Wait: true,
                    WaitTimeout: run.Plan.ComposeWaitTimeout),
                "compose.up", DeploymentStep.Container, FailureCodes.ContainerFailed, ct);
        }
        catch (DeploymentFailure f) when (f.Code != FailureCodes.ServerUnavailable && !ct.IsCancellationRequested)
        {
            // `compose up` says only "exit status 1": which service stopped, and what it printed, is the useful part.
            var failed = await ReportServicesAsync(run, project, null);
            if (failed.Length == 0) throw;
            throw new DeploymentFailure(f.Step, f.Code, $"{f.Message}. {failed}", f);
        }
        if (outcome.CommitSha is { Length: > 0 } commit) d.CommitSha = commit;
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
        {
            await ReportServicesAsync(run, project, outcome.Services);
            throw new DeploymentFailure(DeploymentStep.HealthCheck, FailureCodes.HealthTimeout,
                "Services not running or unhealthy: " + string.Join(", ", bad.Select(s => $"{s.Service} ({s.State}/{s.Health})")));
        }
        d.CompleteStep(DeploymentStep.HealthCheck, run.Clock.UtcNow);
        await run.SaveAsync(ct);

        return outcome.Services.Select(s => s.ContainerId).ToList();
    }

    /// <summary>
    /// Best effort, after a failed start: every service's state in the deployment's log, then the last lines of the output of each one that is
    /// not running and healthy. Returns those services in one line for the failure message ("" when none could be named).
    /// </summary>
    internal static async Task<string> ReportServicesAsync(DeploymentRun run, ComposeProjectSpec project, IReadOnlyList<ComposeServiceInfo>? known)
    {
        var services = known;
        if (services is null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                services = (await run.ExecuteAsync<ComposePsCommand, ComposeOutcome>(
                    new ComposePsCommand(project), "compose.ps.failed", DeploymentStep.Container, FailureCodes.ContainerFailed, cts.Token)).Services;
            }
            catch (Exception)
            {
                await run.NarrateAsync("The state of the services could not be read.");
                return "";
            }
        }

        if (services.Count == 0)
        {
            await run.NarrateAsync("No container of the project was created.");
            return "";
        }

        var ordered = services.OrderBy(s => s.Service, StringComparer.Ordinal).ToList();
        foreach (var s in ordered) await run.NarrateAsync($"Service {s.Service}: {Describe(s)}.");
        // A service that ran and stopped is the cause; one that was only created waited for it (and has no output).
        var failed = ordered.Where(s => !IsUp(s)).OrderBy(s => s.State == ContainerRunState.Created).ToList();
        foreach (var s in failed.Where(s => s.State != ContainerRunState.Created)) await run.CopyContainerLogsAsync(s.ContainerId, s.Service);
        return string.Join("; ", failed.Select(s => $"{s.Service}: {Describe(s)}"));
    }

    private static bool IsUp(ComposeServiceInfo s) =>
        s.State == ContainerRunState.Running && s.Health is not (ContainerHealthState.Unhealthy or ContainerHealthState.Starting);

    private static string Describe(ComposeServiceInfo s) => s.State switch
    {
        ContainerRunState.Running => s.Health switch
        {
            ContainerHealthState.Healthy => "running, healthy",
            ContainerHealthState.Unhealthy => "running, unhealthy (its health check fails)",
            ContainerHealthState.Starting => "running, health check not passed yet",
            _ => "running",
        },
        ContainerRunState.Exited or ContainerRunState.Dead => $"exited with code {s.ExitCode}",
        ContainerRunState.Restarting => $"restarting (last exit code {s.ExitCode})",
        ContainerRunState.Created => "created but not started",
        _ => s.State.ToString().ToLowerInvariant(),
    };
}
