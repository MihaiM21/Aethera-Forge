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
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var payload = context.GetPayload<DeployPayload>();

        var deployment = await db.Deployments.Include(d => d.Steps).FirstOrDefaultAsync(d => d.Id == payload.DeploymentId, cancellationToken)
                         ?? throw JobFailedException.Permanent("deployment.not_found", "The deployment does not exist", failedStep: "start");
        if (deployment.IsTerminal || deployment.Status == DeploymentStatus.Running) return; // already finished (redelivery)
        deployment.JobId ??= context.JobId;

        var app = await LoadApplicationAsync(db, deployment.WorkloadId, cancellationToken)
                  ?? throw JobFailedException.Permanent("application.not_found", "The application no longer exists", failedStep: "start");
        var previous = app.CurrentDeploymentId is { } cur ? await db.Deployments.FirstOrDefaultAsync(d => d.Id == cur, cancellationToken) : null;

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

        var resolver = sp.GetRequiredService<IServerTransportResolver>();
        var proxyOptions = sp.GetRequiredService<IOptions<ProxyOptions>>().Value;
        var proxy = sp.GetRequiredService<IProxyProvider>();
        if (snapshot.Domains.Count > 0 && await EnsureProxyAsync(db, resolver, proxy, proxyOptions, clock, deployment, context, cancellationToken) is { } proxyFailure)
        {
            await RestoreStatusAsync(db, clock, app, previous, proxyFailure, failed: true);
            throw JobFailedException.Permanent("proxy.failed", "The reverse proxy could not be set up", proxyFailure, "Domain");
        }

        var runner = new DeploymentRunner(new ResolvingServerTransport(resolver), sp.GetServices<IDeploymentStrategy>(), clock);
        var plan = DeploymentPlanBuilder.Build(snapshot, new PlanContext
        {
            ServerId = deployment.ServerId, DeploymentId = deployment.Id, DeploymentNumber = deployment.Number, JobId = context.JobId,
            OrganizationId = context.Job.OrganizationId,
            PreviousContainer = previous?.ContainerIds.FirstOrDefault(),
            ResolveSecret = (id, version) => secrets.TryGetValue((id, version), out var v) ? v : throw new InvalidOperationException("A pinned secret version is missing."),
            GitCredential = gitCredential, Registry = registry, ExistingImage = rollbackTarget?.ImageRef,
            Proxy = proxy, ProxyNetwork = proxyOptions.Network, OnLog = null,
        });

        await context.Log.WriteSystemAsync($"Deploying {app.Name} #{deployment.Number} with the {deployment.Strategy} strategy.", cancellationToken);
        var result = await runner.RunAsync(deployment, plan, async (_, ct) => await db.SaveChangesAsync(ct), cancellationToken);

        switch (result)
        {
            case DeploymentRunResult.Running:
                await PromoteAsync(db, sp, clock, app, deployment, previous, cancellationToken);
                await context.Log.WriteSystemAsync($"Deployment #{deployment.Number} is running.", cancellationToken);
                context.SetResult(new { deploymentId = deployment.Id, number = deployment.Number, image = deployment.ImageRef });
                return;

            case DeploymentRunResult.Cancelled:
                await RestoreStatusAsync(db, clock, app, previous, "Deployment cancelled.", failed: false);
                await context.Log.WriteSystemAsync("The deployment was cancelled.", CancellationToken.None);
                return;

            default:
                var failedAfterOldRemoved = previous is not null && deployment.Strategy == DeploymentStrategies.Recreate
                                            && deployment.FailedStep is { } step && step >= DeploymentStep.Container;
                await RestoreStatusAsync(db, clock, app, failedAfterOldRemoved ? null : previous, deployment.FailureReason ?? "Deployment failed.", failed: true);
                var code = deployment.FailureCode ?? "deployment.failed";
                var message = deployment.FailureReason ?? "The deployment failed.";
                throw code == FailureCodes.ServerUnavailable
                    ? JobFailedException.Transient(code, "The server could not be reached", message, deployment.FailedStep?.ToString())
                    : JobFailedException.Permanent(code, "The deployment failed", message, deployment.FailedStep?.ToString());
        }
    }

    /// <summary>Makes sure Traefik runs on the server before routes are attached. Returns a failure message, or null when the proxy is ready.</summary>
    private static async Task<string?> EnsureProxyAsync(
        AetheraDbContext db, IServerTransportResolver resolver, IProxyProvider proxy, ProxyOptions options, IClock clock, Deployment deployment,
        JobContext context, CancellationToken ct)
    {
        var command = proxy.BuildEnsureCommand(new ProxySettings(options.AcmeEmail, options.Network, options.Version, options.Staging));
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
            return message;
        }
        catch (ServerTransportException ex)
        {
            deployment.MarkFailed(DeploymentStep.Domain, ex.Transient ? FailureCodes.ServerUnavailable : "proxy.failed", ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            return ex.Message;
        }
    }

    internal static Task<Application?> LoadApplicationAsync(AetheraDbContext db, Guid id, CancellationToken ct) =>
        db.Applications
            .Include(a => a.GitSource).Include(a => a.BuildConfig).Include(a => a.ImageSource).Include(a => a.ComposeSource)
            .Include(a => a.Ports).Include(a => a.Volumes).Include(a => a.Domains)
            .Include(a => a.EnvironmentVariables).ThenInclude(e => e.Secret)
            .Include(a => a.Networks).ThenInclude(n => n.Network)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == id, ct);

    private static async Task<DeploymentSnapshot> FreezeAsync(
        AetheraDbContext db, IClock clock, Deployment deployment, Application app, Deployment? rollbackTarget, CancellationToken ct)
    {
        if (deployment.Status == DeploymentStatus.InProgress && DeploymentSnapshot.TryFromJson(deployment.ConfigSnapshotJson, out var resumed))
            return resumed!; // resumed after a crash: keep the original snapshot

        DeploymentSnapshot snapshot;
        if (rollbackTarget is not null)
        {
            if (!DeploymentSnapshot.TryFromJson(rollbackTarget.ConfigSnapshotJson, out var frozen))
                throw new UnsupportedSourceException("rollback.snapshot_missing", "The deployment being rolled back to has no stored configuration.");
            snapshot = frozen!;
        }
        else
        {
            snapshot = DeploymentSnapshotFactory.Create(app);
        }

        deployment.Start(snapshot.ToJson(), clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    private static async Task PromoteAsync(
        AetheraDbContext db, IServiceProvider sp, IClock clock, Application app, Deployment deployment, Deployment? previous, CancellationToken ct)
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
    }

    private static async Task RestoreStatusAsync(AetheraDbContext db, IClock clock, Application app, Deployment? previous, string reason, bool failed)
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
    private static async Task CleanupImagesAsync(AetheraDbContext db, IServiceProvider sp, Application app, Deployment deployment, CancellationToken ct)
    {
        try
        {
            var candidates = await db.Images
                .Where(i => i.WorkloadId == app.Id && i.RemovedAt == null && i.DeploymentId != null && i.Deployment!.ImageRef != null)
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

    private static string RegistryHost(string url) =>
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

    private static async Task<string> ReadSecretAsync(IServiceProvider sp, Guid secretId, int? version, JobContext context, CancellationToken ct)
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
