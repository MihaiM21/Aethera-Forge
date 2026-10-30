using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Engine.Proxy;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// <c>application.deploy</c> (ADR 0004, lock key <c>app:&lt;id&gt;</c>): freezes the configuration, runs the deployment pipeline through the
/// engine, then promotes the deployment (supersedes the previous one, updates the workload status, records the image, prunes old images).
/// Resumable: a deployment found InProgress after a crash is re-run from its frozen snapshot, every command carrying a stable idempotency key.
/// </summary>
public sealed class DeployJobHandler : IJobHandler
{
    /// <summary>Rollback points kept per application besides the live deployment.</summary>
    public const int KeepRollbackPoints = 3;

    public string Type => DeploymentService.DeployJobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteDeploymentAsync(context, cancellationToken);
        }
        finally
        {
            // Notifications (ADR 0010): reads the stored outcome, publishes deployment.succeeded / deployment.failed, never throws.
            await Notifications.DeploymentNotifications.PublishOutcomeAsync(context.Services, context.GetPayload<DeployPayload>().DeploymentId);
            // A move (ADR 0012): removes the old server's containers when the deployment runs, puts the workload back when it failed. Never throws.
            await WorkloadMoves.FollowUpAsync(context, context.GetPayload<DeployPayload>());
        }
    }

    /// <summary>
    /// What the handler knows when something goes wrong, so that every way out (an exception nobody planned for, a cancel, a lost secret) can end the
    /// deployment and the workload instead of leaving "Deploying" behind forever.
    /// </summary>
    private sealed class RunState
    {
        public Workload? App { get; set; }
        public Deployment? Previous { get; set; }
    }

    private async Task ExecuteDeploymentAsync(JobContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var payload = context.GetPayload<DeployPayload>();

        var deployment = await db.Deployments.Include(d => d.Steps).FirstOrDefaultAsync(d => d.Id == payload.DeploymentId, cancellationToken)
                         ?? throw JobFailedException.Permanent("deployment.not_found", "The deployment does not exist", failedStep: "start");
        if (deployment.IsTerminal || deployment.Status == DeploymentStatus.Running) return; // already finished (redelivery)
        deployment.JobId ??= context.JobId;

        // The last attempt of the job: a transient failure (the server cannot be reached) ends the deployment now, an earlier one leaves it resumable.
        var finalAttempt = context.Attempt >= context.Job.MaxAttempts;
        var state = new RunState();
        try
        {
            await RunDeploymentAsync(context, db, clock, deployment, state, finalAttempt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await EndUnfinishedAsync(db, clock, deployment, state, cancelled: true, FailureCodes.InternalError, "Deployment cancelled.");
            throw;
        }
        catch (JobFailedException ex)
        {
            // Most paths that throw this settled the deployment first. One that did not (a lost secret) fails it here; a retryable failure
            // with attempts left leaves it in progress for the next attempt.
            var retrying = ex.Error.Retryable && !finalAttempt;
            if (!retrying)
                await EndUnfinishedAsync(db, clock, deployment, state, cancelled: false, ex.Error.Code, context.Secrets.Redact(ex.Error.Detail ?? ex.Error.Title));
            throw;
        }
        catch (ServerTransportException ex) when (ex.Transient)
        {
            // The server could not be reached outside a pipeline step (reading the compose file): the same as a transient step failure.
            if (!finalAttempt)
            {
                await NoteRetryAsync(db, clock, state.App, context.Secrets.Redact(ex.Message));
                throw JobFailedException.Transient(FailureCodes.ServerUnavailable, "The server could not be reached", context.Secrets.Redact(ex.Message), inner: ex);
            }

            await EndUnfinishedAsync(db, clock, deployment, state, cancelled: false, FailureCodes.ServerUnavailable, context.Secrets.Redact(ex.Message));
            throw JobFailedException.Transient(FailureCodes.ServerUnavailable, "The server could not be reached", context.Secrets.Redact(ex.Message), inner: ex);
        }
        catch (Exception ex)
        {
            // Anything else is a bug or a broken environment: the deployment and the workload end Failed with a code and a message that
            // names the kind of error only (the text of an arbitrary exception can hold a connection string or a secret). The worker logs the rest.
            await EndUnfinishedAsync(db, clock, deployment, state, cancelled: false, FailureCodes.InternalError,
                $"An unexpected error stopped the deployment ({ex.GetType().Name}). The details are in the server log.");
            throw;
        }
    }

    /// <summary>
    /// Ends a deployment that the pipeline did not settle: Failed with <paramref name="code"/> (or Cancelled), the step it was at marked failed, and
    /// the workload out of "Deploying". A no-op for a deployment that already ended. Best effort: it runs on error paths and must not hide the cause.
    /// </summary>
    private static async Task EndUnfinishedAsync(
        AetheraDbContext db, IClock clock, Deployment deployment, RunState state, bool cancelled, string code, string message)
    {
        try
        {
            var ended = false;
            for (var attempt = 0; attempt < 3 && !ended; attempt++)
            {
                if (deployment.Status is not (DeploymentStatus.Queued or DeploymentStatus.InProgress)) break;
                var now = clock.UtcNow;
                if (cancelled) deployment.MarkCancelled(now);
                else deployment.MarkFailed(deployment.CurrentStep ?? DeploymentStep.Source, code, string.IsNullOrWhiteSpace(message) ? "The deployment failed." : message, now);
                try
                {
                    await db.SaveChangesAsync(CancellationToken.None);
                    ended = true;
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    foreach (var entry in ex.Entries) await entry.ReloadAsync(CancellationToken.None);
                }
            }

            if (ended && state.App is { } app)
            {
                // A Recreate deployment that got as far as the container step has removed the old container: nothing is serving any more.
                var oldGone = deployment.Strategy == DeploymentStrategies.Recreate && deployment.CurrentStep is { } step && step >= DeploymentStep.Container;
                await RestoreStatusAsync(db, clock, app, oldGone ? null : state.Previous,
                    cancelled ? "Deployment cancelled." : deployment.FailureReason ?? message, failed: !cancelled);
            }
        }
        catch (Exception)
        {
            // The caller rethrows the original problem; the job's own record of it survives.
        }
    }

    /// <summary>The workload stays "Deploying" while the job waits for its next attempt, and says why.</summary>
    private static async Task NoteRetryAsync(AetheraDbContext db, IClock clock, Workload? app, string reason)
    {
        if (app is null) return;
        try
        {
            await ConcurrencySupport.SaveWithRetryAsync(db, () =>
            {
                app.Status = WorkloadStatus.Deploying;
                app.StatusChangedAt = clock.UtcNow;
                app.StatusReason = $"The server could not be reached; the deployment will be retried. {reason}";
            }, CancellationToken.None);
        }
        catch (Exception)
        {
            // Only a status line.
        }
    }

    private async Task RunDeploymentAsync(
        JobContext context, AetheraDbContext db, IClock clock, Deployment deployment, RunState state, bool finalAttempt, CancellationToken cancellationToken)
    {
        var sp = context.Services;

        var app = state.App = await LoadWorkloadAsync(db, deployment.WorkloadId, cancellationToken)
                  ?? throw JobFailedException.Permanent("application.not_found", "The application no longer exists", failedStep: "start");
        await WorkloadMoves.BeginAsync(db, app, deployment, context.GetPayload<DeployPayload>(), context, cancellationToken);
        var previous = state.Previous = app.CurrentDeploymentId is { } cur ? await db.Deployments.FirstOrDefaultAsync(d => d.Id == cur, cancellationToken) : null;

        Deployment? rollbackTarget = deployment.RollbackOfDeploymentId is { } rb
            ? await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == rb, cancellationToken) : null;

        DeploymentSnapshot snapshot;
        try
        {
            snapshot = await FreezeAsync(db, clock, deployment, app, rollbackTarget, cancellationToken);
        }
        catch (UnsupportedSourceException ex)
        {
            deployment.Start("{}", clock.UtcNow);
            deployment.MarkFailed(DeploymentStep.Source, ex.Code, ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            throw JobFailedException.Permanent(ex.Code, "The application cannot be deployed", ex.Message, "source");
        }

        var secrets = await ResolveSecretsAsync(sp, snapshot, context, cancellationToken);
        var registry = await ResolveRegistryAsync(sp, snapshot, context, cancellationToken);
        var gitCredential = await ResolveGitCredentialAsync(sp, snapshot, context, cancellationToken);

        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            app.Status = WorkloadStatus.Deploying;
            app.StatusChangedAt = clock.UtcNow;
            app.StatusReason = $"Deployment #{deployment.Number} in progress";
        }, cancellationToken);

        var buildRecord = await PrepareBuildRecordAsync(db, clock, deployment, snapshot, context, cancellationToken);
        var resolver = sp.GetRequiredService<IServerTransportResolver>();
        var proxyOptions = sp.GetRequiredService<IOptions<ProxyOptions>>().Value;
        var proxy = sp.GetRequiredService<IProxyProvider>();

        // Compose (ADR 0010): which services the file has, so the override can name them all and a domain's service is checked.
        IReadOnlyList<ComposeServiceDefinition>? composeServices = null;
        if (snapshot.Compose is { } composeSnapshot)
        {
            try
            {
                composeServices = await DiscoverComposeServicesAsync(snapshot, composeSnapshot, secrets, gitCredential, resolver, deployment, context, cancellationToken);
            }
            catch (ComposeDiscoveryException ex)
            {
                deployment.MarkFailed(DeploymentStep.Source, ex.Code, ex.Message, clock.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
                await RestoreStatusAsync(db, clock, app, previous, ex.Message, failed: true);
                throw JobFailedException.Permanent(ex.Code, "The compose file cannot be deployed", ex.Message, "Source");
            }
        }

        snapshot = await AssignAutomaticDomainAsync(db, app, deployment, snapshot, rollbackTarget, composeServices, proxyOptions, context, cancellationToken);

        if (snapshot.Domains.Count > 0 && await EnsureProxyAsync(db, resolver, proxy, proxyOptions, sp, clock, deployment, finalAttempt, context, cancellationToken) is { } proxyFailure)
        {
            if (proxyFailure.Retry)
            {
                // Not marked failed: the job runs again and resumes this deployment.
                await NoteRetryAsync(db, clock, app, proxyFailure.Message);
                throw JobFailedException.Transient(FailureCodes.ServerUnavailable, "The server could not be reached", proxyFailure.Message, "Domain");
            }

            await RestoreStatusAsync(db, clock, app, previous, proxyFailure.Message, failed: true);
            if (deployment.FailureCode == FailureCodes.ServerUnavailable)
                throw JobFailedException.Transient(FailureCodes.ServerUnavailable, "The server could not be reached", proxyFailure.Message, "Domain");
            throw JobFailedException.Permanent("proxy.failed", "The reverse proxy could not be set up", proxyFailure.Message, "Domain");
        }

        var runner = new DeploymentRunner(new ResolvingServerTransport(resolver), sp.GetServices<IDeploymentStrategy>(), clock);
        var meshAddress = await db.Servers.AsNoTracking().Where(s => s.Id == deployment.ServerId).Select(s => s.MeshAddress).FirstOrDefaultAsync(cancellationToken);
        DeploymentPlan plan;
        try
        {
            // Build elsewhere and build quota (ADR 0012): the build server, its registry and quota, and the registry of a rollback target that
            // came through one. A deployment that builds nothing resolves none of it.
            var buildServer = snapshot.Build is not null && snapshot.Git is not null && snapshot.Image is null && snapshot.Compose is null && rollbackTarget is null
                ? await BuildServerContextResolver.ResolveAsync(sp, snapshot, deployment.ServerId, context, cancellationToken)
                : null;
            var remoteRollback = rollbackTarget is { BuildServerId: not null, ImageRef.Length: > 0 };
            var rollbackRegistry = remoteRollback ? await BuildServerContextResolver.RegistryForReferenceAsync(sp, rollbackTarget!, context, cancellationToken) : null;
            plan = DeploymentPlanBuilder.Build(snapshot, new PlanContext
            {
                ServerId = deployment.ServerId, DeploymentId = deployment.Id, DeploymentNumber = deployment.Number, JobId = context.JobId, BuildId = buildRecord?.Id,
                OrganizationId = context.Job.OrganizationId,
                // The live deployment may be on another server (a move): there is no container of it here to replace.
                PreviousContainer = previous is not null && previous.ServerId == deployment.ServerId ? previous.ContainerIds.FirstOrDefault() : null,
                ResolveSecret = (id, version) => secrets.TryGetValue((id, version), out var v) ? v : throw new InvalidOperationException("A pinned secret version is missing."),
                GitCredential = gitCredential, Registry = buildServer?.Registry ?? rollbackRegistry ?? registry, ExistingImage = rollbackTarget?.ImageRef,
                ExistingImageIsRemote = remoteRollback,
                Proxy = proxy, ProxyNetwork = proxyOptions.Network, OnLog = null,
                WriteLog = (stream, line, ct) => context.Log.WriteAsync(stream, line, ct),
                Narrate = (line, ct) => context.Log.WriteSystemAsync(line, ct),
                AcmeAvailable = await ProxyConfigurator.AcmeAvailableAsync(db, proxyOptions, cancellationToken), ComposeServices = composeServices,
                MeshAddress = meshAddress,
                BuildServerName = buildServer?.BuildServerName, RunServerName = buildServer?.RunServerName, BuildRegistryPrefix = buildServer?.RegistryPrefix,
                BuildRegistry = buildServer?.Registry, BuilderLimits = buildServer?.BuilderLimits, AcquireBuildSlot = buildServer?.AcquireBuildSlot,
            });
        }
        catch (DeploymentFailure ex)
        {
            // A configuration the server cannot run (a private port without a mesh address): nothing was started.
            deployment.MarkFailed(ex.Step, ex.Code, ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            await RestoreStatusAsync(db, clock, app, previous, ex.Message, failed: true);
            throw JobFailedException.Permanent(ex.Code, "The deployment cannot be run as configured", ex.Message, ex.Step.ToString());
        }
        catch (ProxyRoutingException ex)
        {
            deployment.MarkFailed(DeploymentStep.Domain, "proxy.failed", ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            await RestoreStatusAsync(db, clock, app, previous, ex.Message, failed: true);
            throw JobFailedException.Permanent("proxy.failed", "The domains cannot be routed", ex.Message, "Domain");
        }

        var (effectiveStrategy, strategyNote) = DeploymentRunner.ChooseStrategy(deployment.Strategy, plan);
        await context.Log.WriteSystemAsync($"Deploying {app.Name} #{deployment.Number} with the {effectiveStrategy} strategy.", cancellationToken);
        if (strategyNote is not null) await context.Log.WriteSystemAsync(strategyNote, cancellationToken);
        var result = await runner.RunAsync(deployment, plan, async (_, ct) => await db.SaveChangesAsync(ct), cancellationToken, finalAttempt);
        if (buildRecord is not null) await FinishBuildRecordAsync(db, clock, buildRecord, deployment, plan);

        switch (result)
        {
            case DeploymentRunResult.Retry:
                var transient = runner.TransientFailure!;
                await context.Log.WriteSystemAsync($"The server could not be reached ({transient.Message}); the deployment will be retried.", CancellationToken.None);
                await NoteRetryAsync(db, clock, app, transient.Message);
                throw JobFailedException.Transient(transient.Code, "The server could not be reached", transient.Message, transient.Step.ToString());

            case DeploymentRunResult.Running:
                await RememberDetectedPortAsync(db, app, plan.RanWithDetectedPort, cancellationToken);
                await PromoteAsync(db, sp, clock, app, deployment, previous, cancellationToken);
                await context.Log.WriteSystemAsync($"Deployment #{deployment.Number} is running.", cancellationToken);
                context.SetResult(new { deploymentId = deployment.Id, number = deployment.Number, image = deployment.ImageRef });
                return;

            case DeploymentRunResult.Cancelled:
                await RestoreStatusAsync(db, clock, app, previous, "Deployment cancelled.", failed: false);
                await context.Log.WriteSystemAsync("The deployment was cancelled.", CancellationToken.None);
                return;

            default:
                // Recreate brings the old container back when the new one fails (PreviousRestored); only when that did not work is nothing serving.
                var failedAfterOldRemoved = previous is not null && effectiveStrategy == DeploymentStrategies.Recreate && !plan.PreviousRestored
                                            && deployment.FailedStep is { } step && step >= DeploymentStep.Container;
                await RestoreStatusAsync(db, clock, app, failedAfterOldRemoved ? null : previous, deployment.FailureReason ?? "Deployment failed.", failed: true);
                var code = deployment.FailureCode ?? "deployment.failed";
                var message = deployment.FailureReason ?? "The deployment failed.";
                throw code == FailureCodes.ServerUnavailable
                    ? JobFailedException.Transient(code, "The server could not be reached", message, deployment.FailedStep?.ToString())
                    : JobFailedException.Permanent(code, "The deployment failed", message, deployment.FailedStep?.ToString());
        }
    }

    /// <summary>
    /// Creates (or, when resuming, reuses) the <see cref="Build"/> row of a source build before the build starts, so the UI can follow
    /// its log stream (<c>build:&lt;id&gt;</c>) live. Null for deployments that do not build.
    /// </summary>
    private static async Task<Build?> PrepareBuildRecordAsync(
        AetheraDbContext db, IClock clock, Deployment deployment, DeploymentSnapshot snapshot, JobContext context, CancellationToken ct)
    {
        if (snapshot.Git is null || snapshot.Compose is not null || snapshot.Image is not null || deployment.RollbackOfDeploymentId is not null) return null;
        if (deployment.BuildId is { } existingId
            && await db.Builds.FirstOrDefaultAsync(b => b.Id == existingId && b.Status == BuildStatus.Running, ct) is { } running)
            return running;

        var attempt = await db.Builds.CountAsync(b => b.DeploymentId == deployment.Id, ct) + 1;
        var build = new Build
        {
            DeploymentId = deployment.Id, Attempt = attempt, Engine = snapshot.Build?.Engine ?? BuildEngines.Dockerfile, JobId = context.JobId,
            Platform = snapshot.Build?.TargetPlatform,
        };
        build.Start(clock.UtcNow);
        db.Builds.Add(build);
        deployment.BuildId = build.Id;
        await db.SaveChangesAsync(ct);
        return build;
    }

    private static async Task FinishBuildRecordAsync(AetheraDbContext db, IClock clock, Build build, Deployment deployment, DeploymentPlan plan)
    {
        var step = deployment.Steps.FirstOrDefault(s => s.Step == DeploymentStep.Build);
        var status = step?.Status switch
        {
            StepStatus.Succeeded => BuildStatus.Succeeded,
            StepStatus.Cancelled => BuildStatus.Cancelled,
            _ => deployment.Status == DeploymentStatus.Cancelled ? BuildStatus.Cancelled : BuildStatus.Failed,
        };
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            if (build.Status != BuildStatus.Running) return;
            if (plan.BuildResult is { } r)
            {
                build.CommitSha = r.CommitSha;
                build.ResultImage = r.Tags.FirstOrDefault();
                build.ResultImageDigest = r.Digest;
                build.ResultImageSizeBytes = r.SizeBytes;
                build.CacheHit = r.CacheHit;
                build.Platform = r.Platform is { Length: > 0 } ? r.Platform : build.Platform;
            }
            build.Finish(status, clock.UtcNow);
        }, CancellationToken.None);
    }

    /// <summary>Ports a web server usually listens on: the service of a compose file that gets the automatic domain is the first one exposing one.</summary>
    private static readonly int[] WebPorts = [80, 8080, 3000, 8000, 5000, 4000, 8888, 443];

    private static readonly string[] WebServiceNames = ["web", "app", "frontend", "nginx", "server", "site", "ui", "api"];

    /// <summary>
    /// Gives a workload that has no domain its automatic one (ADR 0011) on its first deployment when it did not get it on creation (a compose
    /// application, whose services are only known now; or a server whose address was unknown then). The domain joins the frozen snapshot.
    /// </summary>
    private static async Task<DeploymentSnapshot> AssignAutomaticDomainAsync(
        AetheraDbContext db, Workload app, Deployment deployment, DeploymentSnapshot snapshot, Deployment? rollbackTarget,
        IReadOnlyList<ComposeServiceDefinition>? composeServices, ProxyOptions options, JobContext context, CancellationToken ct)
    {
        if (app.AutoDomainAssigned || rollbackTarget is not null || snapshot.Domains.Count > 0) return snapshot;

        string? service = null;
        int? port = null;
        if (snapshot.Compose is not null)
        {
            var routable = (composeServices ?? []).Where(s => s.Ports.Count > 0 && s.NetworkMode is null or "bridge" or "default").ToList();
            var pick = routable.Select(s => (Service: s, Port: WebPorts.Cast<int?>().FirstOrDefault(p => s.Ports.Contains(p!.Value))))
                .FirstOrDefault(x => x.Port is not null);
            if (pick.Service is null && routable.FirstOrDefault(s => WebServiceNames.Contains(s.Name)) is { } named)
                pick = (named, named.Ports[0]);
            if (pick.Service is null) return snapshot; // no service that looks like a web server: nothing is guessed
            (service, port) = (pick.Service.Name, pick.Port);
        }
        else if (app is Service && !app.Ports.Any(p => p.IsHttp))
        {
            return snapshot; // a database: nothing to route
        }

        var https = await ProxyConfigurator.AcmeAvailableAsync(db, options, ct);
        var outcome = await DefaultDomainService.AssignAsync(db, app, service, https, ct);
        if (outcome.Domain is not { } domain)
        {
            if (outcome.SkippedBecause is { } why) await context.Log.WriteSystemAsync($"No automatic domain: {why}.", ct);
            await db.SaveChangesAsync(ct);
            return snapshot;
        }

        domain.TargetPort = port;
        var amended = snapshot with
        {
            Domains = [.. snapshot.Domains, new DomainEntry(domain.Hostname, domain.PathPrefix, domain.HttpsEnabled, port, ComposeService: service)],
        };
        deployment.AmendSnapshot(amended.ToJson());
        await db.SaveChangesAsync(ct);
        await context.Log.WriteSystemAsync(
            $"Automatic domain: {(https ? "https" : "http")}://{domain.Hostname}{(service is null ? "" : $" (service {service})")}", ct);
        return amended;
    }

    /// <summary>A port read from the image is stored on a workload that declares none, so the UI shows it and later deployments keep it.</summary>
    private static async Task RememberDetectedPortAsync(AetheraDbContext db, Workload app, int? port, CancellationToken ct)
    {
        if (port is not { } p || app.Ports.Count > 0) return;
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            if (app.Ports.Count == 0) app.Ports.Add(new WorkloadPort { WorkloadId = app.Id, ContainerPort = p, IsHttp = true });
        }, ct);
    }

    /// <summary>A proxy that could not be set up. <see cref="Retry"/>: the server was unreachable and the job has attempts left; the deployment is left open.</summary>
    private sealed record ProxyFailure(string Message, bool Retry);

    /// <summary>Makes sure Traefik runs on the server before routes are attached. Returns the failure, or null when the proxy is ready.</summary>
    private static async Task<ProxyFailure?> EnsureProxyAsync(
        AetheraDbContext db, IServerTransportResolver resolver, IProxyProvider proxy, ProxyOptions options, IServiceProvider sp, IClock clock, Deployment deployment,
        bool finalAttempt, JobContext context, CancellationToken ct)
    {
        var command = await ProxyConfigurator.BuildEnsureCommandAsync(
            db, sp.GetRequiredService<ISecretProtector>(), proxy, options, deployment.ServerId, context.Secrets.Register, ct);
        try
        {
            await context.Log.WriteSystemAsync("Making sure the reverse proxy is running.", ct);
            var resolved = await resolver.ExecuteAsync<ProxyEnsureCommand, ProxyEnsured>(deployment.ServerId, command, new CommandOptions
            {
                IdempotencyKey = context.IdempotencyKey("proxy.ensure"), JobId = context.JobId, OrganizationId = context.Job.OrganizationId,
            }, ct);
            if (resolved.Outcome.Succeeded) return null;
            var message = $"The agent could not start the proxy: {resolved.Outcome.ErrorMessage ?? resolved.Outcome.Status.ToString()}";
            deployment.MarkFailed(DeploymentStep.Domain, "proxy.failed", message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            return new ProxyFailure(message, Retry: false);
        }
        catch (ServerTransportException ex)
        {
            if (ex.Transient && !finalAttempt) return new ProxyFailure(ex.Message, Retry: true);
            deployment.MarkFailed(DeploymentStep.Domain, ex.Transient ? FailureCodes.ServerUnavailable : "proxy.failed", ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            return new ProxyFailure(ex.Message, Retry: false);
        }
    }

    /// <summary>
    /// The services of the compose file (ADR 0010): an inline file is read here, a file in the repository by the agent (which also checks it
    /// against the policy before anything is built).
    /// </summary>
    private static async Task<IReadOnlyList<ComposeServiceDefinition>?> DiscoverComposeServicesAsync(
        DeploymentSnapshot snapshot, ComposeSnapshot compose, Dictionary<(Guid, int), string> secrets, GitCredentialSpec? gitCredential,
        IServerTransportResolver resolver, Deployment deployment, JobContext context, CancellationToken ct)
    {
        if (compose.FilePath is not { Length: > 0 } filePath) return ComposeServiceDiscovery.FromInline(compose.Content);

        var git = snapshot.Git ?? throw new ComposeDiscoveryException("source.not_configured", "A compose file in the repository needs a git source.");
        await context.Log.WriteSystemAsync("Reading the compose file from the repository.", ct);
        var project = new ComposeProjectSpec(DeploymentPlanBuilder.ComposeProjectName(snapshot), "")
        {
            Git = new GitSourceSpec(git.Url, git.Branch, git.CommitPin, Depth: git.CommitPin is null ? 1 : 0, Credentials: gitCredential),
            ComposePath = filePath,
            Env = snapshot.Env.Where(e => e.Runtime || e.Build)
                .Select(e => e.SecretId is { } id
                    ? EnvVarSpec.OfSecret(e.Key, secrets.GetValueOrDefault((id, e.SecretVersion ?? 0), ""))
                    : EnvVarSpec.OfPlain(e.Key, e.Value ?? "")).ToList(),
        };
        var inspection = await ComposeServiceDiscovery.InspectAsync(resolver, deployment.ServerId, project, new CommandOptions
        {
            IdempotencyKey = context.IdempotencyKey("compose.inspect"), JobId = context.JobId, OrganizationId = context.Job.OrganizationId,
        }, ct);
        return inspection.Services;
    }

    /// <summary>Loads an application or a service with everything the snapshot reads.</summary>
    internal static async Task<Workload?> LoadWorkloadAsync(AetheraDbContext db, Guid id, CancellationToken ct)
    {
        var workload = await db.Workloads
            .Include(w => w.Ports).Include(w => w.Volumes).Include(w => w.Domains)
            .Include(w => w.EnvironmentVariables).ThenInclude(e => e.Secret)
            .Include(w => w.Networks).ThenInclude(n => n.Network)
            .AsSplitQuery()
            .FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workload is Application app)
        {
            var entry = db.Entry(app);
            await entry.Reference(a => a.GitSource).LoadAsync(ct);
            await entry.Reference(a => a.BuildConfig).LoadAsync(ct);
            await entry.Reference(a => a.ImageSource).LoadAsync(ct);
            await entry.Reference(a => a.ComposeSource).LoadAsync(ct);
        }

        return workload;
    }

    private static async Task<DeploymentSnapshot> FreezeAsync(
        AetheraDbContext db, IClock clock, Deployment deployment, Workload app, Deployment? rollbackTarget, CancellationToken ct)
    {
        if (deployment.Status == DeploymentStatus.InProgress && DeploymentSnapshot.TryFromJson(deployment.ConfigSnapshotJson, out var resumed))
            return resumed!; // resumed after a crash: keep the original snapshot

        DeploymentSnapshot snapshot;
        if (rollbackTarget is not null)
        {
            if (!DeploymentSnapshot.TryFromJson(rollbackTarget.ConfigSnapshotJson, out var frozen))
                throw new UnsupportedSourceException("rollback.snapshot_missing", "The deployment being rolled back to has no stored configuration.");
            snapshot = frozen!;
            // A compose file read from the repository follows the branch: a rollback pins the commit the target deployment ran.
            if (snapshot.Compose?.FilePath is not null && snapshot.Git is { CommitPin: null } git && rollbackTarget.CommitSha is { Length: > 0 } sha)
                snapshot = snapshot with { Git = git with { CommitPin = sha } };
        }
        else
        {
            // A webhook push deploys the pushed branch at the pushed commit, not the application's configured branch.
            snapshot = DeploymentSnapshotFactory.ApplyTrigger(DeploymentSnapshotFactory.Create(app), deployment.Trigger, deployment.Ref, deployment.CommitSha);
        }

        deployment.Start(snapshot.ToJson(), clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    private static async Task PromoteAsync(
        AetheraDbContext db, IServiceProvider sp, IClock clock, Workload app, Deployment deployment, Deployment? previous, CancellationToken ct)
    {
        var now = clock.UtcNow;

        if (deployment.ImageRef is { Length: > 0 } image && !db.Images.Any(i => i.DeploymentId == deployment.Id))
        {
            var (repo, tag) = SplitReference(image);
            db.Images.Add(new ImageRecord
            {
                WorkloadId = app.Id, DeploymentId = deployment.Id, ServerId = app.ServerId, Repository = repo, Tag = tag, Digest = deployment.ImageDigest, ImageCreatedAt = now,
            });
        }
        var supersede = previous is not null && previous.Id != deployment.Id;
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            if (supersede && previous!.Status is DeploymentStatus.Running or DeploymentStatus.Stopped) previous.MarkSuperseded(now);
            app.CurrentDeploymentId = deployment.Id;
            app.DesiredState = DesiredState.Running;
            app.Status = WorkloadStatus.Running;
            app.StatusChangedAt = now;
            app.StatusObservedAt = now;
            app.StatusReason = null;
        }, ct);
        await CleanupImagesAsync(db, sp, app, deployment, ct);
        await BuildServerContextResolver.CleanUpBuildServerAsync(db, sp, app, deployment);
    }

    internal static async Task RestoreStatusAsync(AetheraDbContext db, IClock clock, Workload app, Deployment? previous, string reason, bool failed)
    {
        var now = clock.UtcNow;
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            if (previous is not null && previous.Status == DeploymentStatus.Running)
            {
                app.Status = WorkloadStatus.Running;
                app.StatusReason = failed ? $"Latest deployment failed; still serving #{previous.Number}. {reason}" : null;
            }
            else
            {
                app.Status = failed ? WorkloadStatus.Failed : app.CurrentDeploymentId is null ? WorkloadStatus.NotDeployed : WorkloadStatus.Unknown;
                app.StatusReason = reason;
            }
            app.StatusChangedAt = now;
        }, CancellationToken.None);
    }

    /// <summary>Removes images beyond the rollback retention. Best effort: a failure here never fails the deployment.</summary>
    private static async Task CleanupImagesAsync(AetheraDbContext db, IServiceProvider sp, Workload app, Deployment deployment, CancellationToken ct)
    {
        try
        {
            var candidates = await db.Images
                .Where(i => i.WorkloadId == app.Id && i.ServerId == app.ServerId && i.RemovedAt == null && i.DeploymentId != null && i.Deployment!.ImageRef != null)
                .Select(i => new { i.Id, Image = i.Deployment!.ImageRef!, DeploymentId = i.DeploymentId!.Value, i.CreatedAt, i.Deployment.IsRollbackPoint })
                .ToListAsync(ct);
            var toRemove = ImagePolicy.ImagesToRemove(
                candidates.Select(c => new ImagePolicy.Candidate(c.Image, c.DeploymentId, c.CreatedAt, c.IsRollbackPoint)), deployment.Id, KeepRollbackPoints);
            if (toRemove.Count == 0) return;

            var resolver = sp.GetRequiredService<IServerTransportResolver>();
            foreach (var image in toRemove)
            {
                var resolved = await resolver.ExecuteAsync<ImageRemoveCommand, Unit>(
                    app.ServerId, new ImageRemoveCommand(image, Force: false), CommandOptions.For($"cleanup:{deployment.Id}:{image}"), ct);
                if (!resolved.Outcome.Succeeded && resolved.Outcome.ErrorCode != CommandErrorCode.NotFound) continue; // in use or busy: keep it
                foreach (var row in candidates.Where(c => c.Image == image))
                    (await db.Images.FindAsync([row.Id], ct))!.RemovedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // Image cleanup is housekeeping; the maintenance prune job catches up later.
        }
    }

    private static (string Repository, string Tag) SplitReference(string reference)
    {
        var at = reference.IndexOf('@');
        if (at > 0) return (reference[..at], reference[at..]);
        var colon = reference.LastIndexOf(':');
        return colon > reference.LastIndexOf('/') ? (reference[..colon], reference[(colon + 1)..]) : (reference, "latest");
    }

    // ---- secret resolution (values are decrypted in memory only and registered for log redaction) -----------------------------------------

    private static async Task<Dictionary<(Guid, int), string>> ResolveSecretsAsync(
        IServiceProvider sp, DeploymentSnapshot snapshot, JobContext context, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, int), string>();
        foreach (var e in snapshot.Env.Where(e => e.SecretId is not null))
        {
            var key = (e.SecretId!.Value, e.SecretVersion ?? 0);
            if (result.ContainsKey(key)) continue;
            result[key] = await ReadSecretAsync(sp, key.Item1, key.Item2 == 0 ? null : key.Item2, context, ct);
        }

        // Basic auth htpasswd lines of the domains (bcrypt hashes, still not for the logs).
        foreach (var d in snapshot.Domains.Where(d => d.BasicAuthSecretId is not null))
        {
            var key = (d.BasicAuthSecretId!.Value, d.BasicAuthSecretVersion ?? 0);
            if (result.ContainsKey(key)) continue;
            result[key] = await ReadSecretAsync(sp, key.Item1, key.Item2 == 0 ? null : key.Item2, context, ct);
        }
        return result;
    }

    private static async Task<RegistryCredentials?> ResolveRegistryAsync(IServiceProvider sp, DeploymentSnapshot snapshot, JobContext context, CancellationToken ct)
    {
        if (snapshot.Image?.RegistryId is not { } id) return null;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var registry = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (registry is null) return null;
        var password = registry.PasswordSecretId is { } sid ? await ReadSecretAsync(sp, sid, null, context, ct) : null;
        return new RegistryCredentials(RegistryHost(registry.Url), registry.Username ?? "", password is null ? null : new SecretValue(password));
    }

    internal static string RegistryHost(string url) =>
        Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var uri) ? uri.Authority : url;

    private static async Task<GitCredentialSpec?> ResolveGitCredentialAsync(IServiceProvider sp, DeploymentSnapshot snapshot, JobContext context, CancellationToken ct)
    {
        if (snapshot.Git?.CredentialId is not { } id) return null;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var credential = await db.GitCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return null;
        var secret = await ReadSecretAsync(sp, credential.SecretId, null, context, ct);
        return credential.Kind == GitCredentialKind.DeployKey
            ? new GitCredentialSpec(credential.Id.ToString(), SshPrivateKey: new SecretValue(secret))
            : new GitCredentialSpec(credential.Id.ToString(), credential.Username, new SecretValue(secret));
    }

    internal static async Task<string> ReadSecretAsync(IServiceProvider sp, Guid secretId, int? version, JobContext context, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AetheraDbContext>();
        var protector = sp.GetRequiredService<ISecretProtector>();
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == secretId, ct)
                     ?? throw JobFailedException.Permanent("secret.not_found", "A secret used by this deployment no longer exists", failedStep: "secrets");
        var v = version ?? secret.CurrentVersion;
        var row = await db.SecretVersions.AsNoTracking().FirstOrDefaultAsync(x => x.SecretId == secretId && x.Version == v, ct)
                  ?? throw JobFailedException.Permanent("secret.version_missing", "A pinned secret version no longer exists", failedStep: "secrets");
        try
        {
            var value = new ProtectedValue(row.Ciphertext, row.Nonce, row.MasterKeyVersion) { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce };
            var plaintext = Encoding.UTF8.GetString(protector.Unprotect(value, $"secret:{secret.Id}:v{row.Version}"));
            context.Secrets.Register(plaintext);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw JobFailedException.Permanent("secret.undecryptable", "A secret could not be decrypted", "Check the configured master key.", "secrets", ex);
        }
    }
}
