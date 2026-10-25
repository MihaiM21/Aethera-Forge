using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Auditing;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Backups;

/// <summary>Job types and lock keys of the backup module.</summary>
public static class BackupJobs
{
    public const string RunType = "backup.run";
    public const string RestoreType = "backup.restore";

    /// <summary>
    /// A backup of a workload takes the workload's lock (the one of deployments, lifecycle and restores), so it never overlaps a deployment of it;
    /// Aethera's own database has a lock per organization.
    /// </summary>
    public static string LockKey(Guid organizationId, Guid? workloadId) =>
        workloadId is { } id ? DeploymentService.LockKey(id) : $"backup:controlplane:{organizationId}";
}

/// <summary>Payload of <c>backup.run</c>: ids only. The run row exists already (created when the job was enqueued).</summary>
public sealed class BackupRunPayload
{
    public Guid PolicyId { get; set; }
    public Guid RunId { get; set; }
    public BackupTrigger Trigger { get; set; }
}

/// <summary>A step of a backup or restore that failed for a reason worth telling the user. The message is shown as is: no secrets in it.</summary>
public sealed class BackupFailure(string code, string message, bool transient = false, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;

    /// <summary>Worth retrying (the agent was unreachable); a refusal or a failed command is not.</summary>
    public bool Transient { get; } = transient;
}

/// <summary>Reads the current value of a secret (only the backup code of the control plane needs plaintext; it registers it for log redaction).</summary>
public sealed class BackupSecretReader(AetheraDbContext db, ISecretProtector protector)
{
    public async Task<string> ReadAsync(Guid secretId, ISecretRedactor? redactor, CancellationToken cancellationToken)
    {
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == secretId, cancellationToken)
                     ?? throw new BackupFailure("secret.not_found", "A secret needed for this backup no longer exists.");
        var row = await db.SecretVersions.AsNoTracking().FirstOrDefaultAsync(v => v.SecretId == secretId && v.Version == secret.CurrentVersion, cancellationToken)
                  ?? throw new BackupFailure("secret.version_missing", "A secret needed for this backup has no value.");
        try
        {
            var value = new ProtectedValue(row.Ciphertext, row.Nonce, row.MasterKeyVersion) { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce };
            var plaintext = protector.Unprotect(value, $"secret:{secret.Id}:v{row.Version}");
            try
            {
                var text = Encoding.UTF8.GetString(plaintext);
                redactor?.Register(text);
                return text;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException ex)
        {
            throw new BackupFailure("secret.undecryptable", "A secret could not be decrypted. Check the configured master key.", inner: ex);
        }
    }
}

/// <summary>Audit events written by backup jobs: no HTTP actor exists there, so the organization and the user who asked come from the job.</summary>
public static class BackupAudit
{
    public static async Task RecordAsync(
        AetheraDbContext db, IClock clock, Job job, string action, string resourceType, Guid? resourceId, object? metadata, CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            OrganizationId = job.OrganizationId,
            ActorType = job.CreatedBy is null ? AuditActorType.System : AuditActorType.User,
            ActorUserId = job.CreatedBy,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            MetadataJson = Redaction.ToJsonObject(metadata),
            OccurredAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Finds the containers of a workload on its server by the <c>aethera.workload</c> label (the way every managed container is labelled).</summary>
public sealed class WorkloadContainers(IServerTransportResolver transports)
{
    public async Task<IReadOnlyList<DockerContainer>> ListAsync(Guid serverId, Guid workloadId, bool runningOnly, JobContext context, CancellationToken cancellationToken)
    {
        var resolved = await transports.ExecuteAsync<ContainerListCommand, IReadOnlyList<DockerContainer>>(
            serverId, new ContainerListCommand(All: !runningOnly, LabelFilters: [$"aethera.workload={workloadId}"]),
            Options(context, $"containers.{Guid.NewGuid():N}"), cancellationToken);
        var containers = resolved.Outcome.EnsureSucceeded();
        return runningOnly ? containers.Where(c => c.State == ContainerRunState.Running).ToList() : containers;
    }

    public static CommandOptions Options(JobContext context, string step) => new()
    {
        IdempotencyKey = context.IdempotencyKey(step), JobId = context.JobId, OrganizationId = context.Job.OrganizationId, ActorUserId = context.Job.CreatedBy,
    };

    public static string NameOf(DockerContainer container) => container.Name.TrimStart('/');
}
