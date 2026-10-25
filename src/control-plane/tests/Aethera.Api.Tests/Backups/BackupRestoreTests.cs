using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Tests.Backups;

/// <summary>Restore (<c>backup.restore</c>): confirmation, the safety backup, stopping, the agent command, starting again, a new service.</summary>
[Collection(BackupsCollection.Name)]
public sealed class BackupRestoreTests(BackupsFixture fixture)
{
    private FakeServerTransport Transport => fixture.Transport;

    private void Reset()
    {
        fixture.Store.Reset();
        fixture.Notifier.Reset();
        Transport.Healthy();
    }

    private static object Body(string name, string target = "same") => new { target, confirm = name };

    private async Task<(BackupStack Stack, string PolicyId, BackupRun Run)> BackedUpAsync(string template = "postgres", object? policy = null)
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture, template);
        var policyId = await stack.CreateDatabasePolicyAsync(policy);
        var run = await stack.BackUpAsync(policyId);
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Transport.Commands.Clear();
        return (stack, policyId, run);
    }

    private static async Task<string> RestoreAsync(BackupStack stack, BackupRun run, object body, HttpClient? client = null)
    {
        var response = await (client ?? stack.Tenant.Admin).PostAsync($"/api/v1/backup-runs/{run.Id}/restore", body);
        var node = await response.ReadAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"restore -> {(int)response.StatusCode}: {node.ToJsonString()}");
        return node.Id();
    }

    private List<string> Order() => Transport.Commands.Select(c => c.Command.GetType().Name).Where(n => n != nameof(ContainerListCommand)).ToList();

    // ---- the request -------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Restoring_needs_an_administrator_the_deploy_scope_and_the_exact_name()
    {
        var (stack, _, run) = await BackedUpAsync();
        var url = $"/api/v1/backup-runs/{run.Id}/restore";
        var t = stack.Tenant;

        await (await t.Developer.PostAsync(url, Body(stack.ServiceName))).AssertProblemAsync(403, "auth.forbidden");
        await (await t.Viewer.PostAsync(url, Body(stack.ServiceName))).AssertProblemAsync(403, "auth.forbidden");
        await (await t.Token(OrganizationRole.Admin, "write").PostAsync(url, Body(stack.ServiceName))).AssertProblemAsync(403, "auth.insufficient_scope");

        var missing = await t.Admin.PostAsync(url, new { target = "same" });
        await missing.AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(stack.ServiceName, (await (await t.Admin.PostAsync(url, new { target = "same", confirm = "wrong" })).ReadAsync())["expectedConfirmation"]!.GetValue<string>());
        await (await t.Admin.PostAsync(url, new { target = "same", confirm = stack.ServiceName.ToUpperInvariant() })).AssertProblemAsync(428, "confirmation.required");
        Assert.Empty(Transport.Received<BackupRestoreCommand>());

        Assert.Contains(("/target", "invalid_enum"), await (await t.Admin.PostAsync(url, new { target = "elsewhere", confirm = stack.ServiceName })).ValidationErrorsAsync());
        Assert.Contains(("/newServiceName", "required"), await (await t.Admin.PostAsync(url, new { target = "new", confirm = stack.ServiceName })).ValidationErrorsAsync());
        await (await t.Admin.PostAsync($"/api/v1/backup-runs/{Guid.NewGuid()}/restore", Body(stack.ServiceName))).AssertProblemAsync(404, "backup_run.not_found");

        var jobId = await RestoreAsync(stack, run, Body(stack.ServiceName), t.Token(OrganizationRole.Admin, "deploy"));
        await stack.WaitForJobAsync(jobId);
    }

    [RequiresDatabaseFact]
    public async Task A_backup_that_failed_or_was_pruned_is_not_a_restore_point()
    {
        var (stack, policyId, run) = await BackedUpAsync();
        await stack.WithDbAsync(async db =>
        {
            var failed = BackupRunner.NewRun(await db.BackupPolicies.FirstAsync(p => p.Id == Guid.Parse(policyId)), Guid.CreateVersion7(), BackupTrigger.Manual, null);
            failed.Status = BackupRunStatus.Failed;
            db.BackupRuns.Add(failed);
            (await db.BackupRuns.FirstAsync(r => r.Id == run.Id)).PrunedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return failed.Id;
        });
        var runs = await stack.RunsAsync(policyId);
        foreach (var r in runs)
        {
            await (await stack.Tenant.Admin.PostAsync($"/api/v1/backup-runs/{r.Id}/restore", Body(stack.ServiceName))).AssertProblemAsync(409, "backup_run.not_restorable");
            Assert.False((await stack.Tenant.Admin.GetJsonAsync($"/api/v1/backup-runs/{r.Id}"))["restorable"]!.GetValue<bool>());
        }

        Assert.Empty(Transport.Received<BackupRestoreCommand>());
    }

    // ---- same ---------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Restoring_a_database_into_itself_takes_a_safety_backup_first_and_sends_the_restore_with_the_checksum()
    {
        var (stack, policyId, run) = await BackedUpAsync();
        var jobId = await RestoreAsync(stack, run, Body(stack.ServiceName));
        var job = await stack.WaitForJobAsync(jobId);
        Assert.True(job.Status == JobStatus.Succeeded, job.ErrorJson);

        Assert.Equal([nameof(BackupRunCommand), nameof(BackupRestoreCommand)], Order()); // postgres is restored while it runs: no stop, no start
        var safety = Transport.Received<BackupRunCommand>().Single();
        var restore = Transport.Received<BackupRestoreCommand>().Single();
        Assert.NotEqual(run.Id.ToString("D"), safety.BackupId);
        Assert.Equal(run.Id.ToString("D"), restore.BackupId);
        Assert.Equal(run.StoredSha256, restore.ExpectedSha256);
        var source = Assert.IsType<S3LocationSpec>(restore.Source);
        Assert.Equal(run.ObjectKey, source.Key);
        Assert.Equal(BackupStack.Secret, source.SecretAccessKey.Value);
        Assert.Equal(run.KeyId, restore.Encryption.KeyId);
        var sink = Assert.IsType<ExecRestoreSink>(restore.Sink);
        Assert.Equal("pg_restore", sink.Argv[0]);
        Assert.Contains("--clean", sink.Argv);
        Assert.Null(Assert.Single(sink.Env!).Plain);

        var runs = await stack.RunsAsync(policyId);
        var preRestore = Assert.Single(runs, r => r.Trigger == BackupTrigger.PreRestore);
        Assert.Equal(BackupRunStatus.Succeeded, preRestore.Status);
        Assert.Equal(safety.BackupId, preRestore.Id.ToString("D"));
        Assert.Empty(fixture.Store.Deleted); // the safety backup does not run retention: it could delete what is being restored

        var audit = await stack.Tenant.AuditAsync(run.Id.ToString());
        Assert.Contains(audit, e => e.Action == "backup.restore_requested");
        var done = Assert.Single(audit, e => e.Action == "backup.restored");
        Assert.Equal(stack.Tenant.Identity.UserId, done.ActorUserId);
        Assert.DoesNotContain(BackupStack.Secret, string.Join(' ', audit.Select(e => e.MetadataJson)));
    }

    [RequiresDatabaseFact]
    public async Task If_the_safety_backup_fails_nothing_is_restored()
    {
        var (stack, _, run) = await BackedUpAsync();
        Transport.Fail<BackupRunCommand, BackupRunOutcome>(CommandErrorCode.OutOfDisk, "no space left on device");

        var job = await stack.WaitForJobAsync(await RestoreAsync(stack, run, Body(stack.ServiceName)));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("backup.restore_safety_backup_failed", job.ErrorJson);
        Assert.Contains("Nothing was changed", job.ErrorJson);
        Assert.Empty(Transport.Received<BackupRestoreCommand>());
        Assert.Empty(Transport.Received<ContainerStopCommand>());
        Assert.Contains(await stack.Tenant.AuditAsync(run.Id.ToString()), e => e.Action == "backup.restore_failed");
    }

    [RequiresDatabaseFact]
    public async Task A_failed_restore_is_reported_and_the_audit_log_says_so()
    {
        var (stack, _, run) = await BackedUpAsync();
        Transport.Fail<BackupRestoreCommand, BackupRestoreOutcome>(CommandErrorCode.ChecksumMismatch, "the stored object does not match the expected SHA-256");

        var job = await stack.WaitForJobAsync(await RestoreAsync(stack, run, Body(stack.ServiceName)));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("backup.restore_failed", job.ErrorJson);
        Assert.Contains("does not match", job.ErrorJson);
        var failed = Assert.Single(await stack.Tenant.AuditAsync(run.Id.ToString(), "backup.restore_failed"));
        Assert.Contains("backup.restore_failed", failed.MetadataJson);
    }

    [RequiresDatabaseFact]
    public async Task Redis_is_stopped_for_the_restore_into_its_data_volume_and_started_again()
    {
        var (stack, _, run) = await BackedUpAsync("redis");
        Assert.Equal("redis", run.Engine);
        Assert.EndsWith(".rdb.tar.gz.aeb", run.ObjectKey);

        var job = await stack.WaitForJobAsync(await RestoreAsync(stack, run, Body(stack.ServiceName)));
        Assert.True(job.Status == JobStatus.Succeeded, job.ErrorJson);

        Assert.Equal([nameof(BackupRunCommand), nameof(ContainerStopCommand), nameof(BackupRestoreCommand), nameof(ContainerStartCommand)], Order());
        var volume = await stack.WithDbAsync(db => db.Volumes.AsNoTracking().FirstAsync(v => v.WorkloadId == Guid.Parse(stack.ServiceId)));
        var sink = Assert.IsType<VolumeRestoreSink>(Transport.Received<BackupRestoreCommand>().Single().Sink);
        Assert.Equal(volume.Name, sink.Volume);
        Assert.Equal("db-1234", Transport.Received<ContainerStopCommand>().Single().Container);
        Assert.Equal("db-1234", Transport.Received<ContainerStartCommand>().Single().Container);
    }

    [RequiresDatabaseFact]
    public async Task The_workload_is_started_again_even_when_the_restore_fails()
    {
        var (stack, _, run) = await BackedUpAsync("redis");
        Transport.Fail<BackupRestoreCommand, BackupRestoreOutcome>(CommandErrorCode.Internal, "tar: short read");

        var job = await stack.WaitForJobAsync(await RestoreAsync(stack, run, Body(stack.ServiceName)));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal([nameof(BackupRunCommand), nameof(ContainerStopCommand), nameof(BackupRestoreCommand), nameof(ContainerStartCommand)], Order());
    }

    [RequiresDatabaseFact]
    public async Task A_volume_backup_restores_into_the_emptied_volume_with_the_workload_stopped()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture, "grafana");
        var volumeId = (await stack.Tenant.Viewer.GetJsonAsync(stack.PoliciesUrl))["targets"]!.AsArray().First(x => x!["kind"]!.GetValue<string>() == "volume")!["volumeId"]!.GetValue<string>();
        var policyId = (await stack.PutPolicyAsync(new { kind = "volume", volumeId, destinationId = stack.DestinationId }))["id"]!.GetValue<string>();
        var run = await stack.BackUpAsync(policyId);
        Transport.Commands.Clear();

        var job = await stack.WaitForJobAsync(await RestoreAsync(stack, run, Body(stack.ServiceName)));
        Assert.True(job.Status == JobStatus.Succeeded, job.ErrorJson);

        Assert.Equal([nameof(BackupRunCommand), nameof(ContainerStopCommand), nameof(BackupRestoreCommand), nameof(ContainerStartCommand)], Order());
        var volume = await stack.WithDbAsync(db => db.Volumes.AsNoTracking().FirstAsync(v => v.Id == Guid.Parse(volumeId)));
        var sink = Assert.IsType<VolumeRestoreSink>(Transport.Received<BackupRestoreCommand>().Single().Sink);
        Assert.Equal(volume.Name, sink.Volume);
        Assert.True(sink.ClearFirst);
        Assert.IsType<VolumeArchiveSource>(Transport.Received<BackupRunCommand>().Single().Source); // the safety backup is a volume archive too

        // a volume cannot be restored into a new service
        var refused = await stack.Tenant.Admin.PostAsync($"/api/v1/backup-runs/{run.Id}/restore", new { target = "new", confirm = stack.ServiceName, newServiceName = "copy" });
        Assert.Contains(("/target", "unsupported"), await refused.ValidationErrorsAsync());
    }

    // ---- new ----------------------------------------------------------------------------------------------------------------------

    private void ScriptDeployment()
    {
        var n = 0;
        Transport
            .On<ImagePullCommand, ImagePulled>(_ => new ImagePulled("sha256:img", "sha256:dig", 1, [], false))
            .On<ImageInspectCommand, DockerImage>(c => new DockerImage("sha256:img", [c.Image], [], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0))
            .On<NetworkCreateCommand, DockerNetwork>(c => new DockerNetwork("n", c.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []))
            .On<VolumeCreateCommand, DockerVolume>(c => new DockerVolume(c.VolumeName, "local", "/var/lib/docker/volumes/x", new Dictionary<string, string>(), null, 0, 0))
            .On<ContainerCreateCommand, ContainerCreated>(c => new ContainerCreated($"cid-{Interlocked.Increment(ref n)}", c.Spec.Name, ContainerRunState.Running, []))
            .On<ContainerRemoveCommand, Unit>(_ => Unit.Value)
            .On<ImageRemoveCommand, Unit>(_ => Unit.Value)
            .On<HealthProbeCommand, HealthProbeOutcome>(_ => new HealthProbeOutcome(true, 1, 0, TimeSpan.FromMilliseconds(5), "ok", DateTimeOffset.UtcNow))
            .On<ProxyEnsureCommand, ProxyEnsured>(_ => new ProxyEnsured("traefik", "proxy-1", "v3.1", true, true, "abc"))
            .On<ContainerListCommand, IReadOnlyList<DockerContainer>>(c => [FakeAgent.Container("c-" + c.LabelFilters![0].Split('=')[1][..8])]);
    }

    [RequiresDatabaseFact]
    public async Task A_backup_is_restored_into_a_new_service_made_from_the_same_template()
    {
        var (stack, _, run) = await BackedUpAsync();
        ScriptDeployment();

        var jobId = await RestoreAsync(stack, run, new { target = "new", confirm = stack.ServiceName, newServiceName = "Restore check" });
        var job = await stack.WaitForJobAsync(jobId);
        Assert.True(job.Status == JobStatus.Succeeded, job.ErrorJson);

        var services = (await stack.Tenant.Viewer.GetJsonAsync("/api/v1/services"))["items"]!.AsArray();
        var created = Assert.Single(services, s => s!["name"]!.GetValue<string>() == "Restore check")!;
        var source = await stack.ServiceAsync();
        Assert.NotEqual(stack.ServiceId, created.Id());
        Assert.Equal("postgres", created["templateKey"]!.GetValue<string>());
        Assert.Equal(source["templateVersion"]!.GetValue<string>(), created["templateVersion"]!.GetValue<string>());
        Assert.Equal(source["image"]!.GetValue<string>(), created["image"]!.GetValue<string>());
        Assert.Equal(stack.EnvironmentId, created["environmentId"]!.GetValue<string>());
        Assert.Equal("running", created["status"]!.GetValue<string>());

        // the restore went into the new service's container with the new service's own generated password
        var restore = Assert.Single(Transport.Received<BackupRestoreCommand>());
        var sink = Assert.IsType<ExecRestoreSink>(restore.Sink);
        Assert.Equal("c-" + created.Id()[..8], sink.Container);
        Assert.Equal(run.Id.ToString("D"), restore.BackupId);
        Assert.Equal(run.StoredSha256, restore.ExpectedSha256);
        Assert.Empty(Transport.Received<BackupRunCommand>()); // nothing to protect in a new service: no safety backup
        Assert.Empty(Transport.Received<ContainerStopCommand>());

        var audit = await stack.Tenant.AuditAsync(created.Id(), "service.created");
        Assert.Single(audit);
        Assert.Equal(stack.Tenant.Identity.UserId, audit[0].ActorUserId);
        Assert.Contains(await stack.Tenant.AuditAsync(run.Id.ToString()), e => e.Action == "backup.restored");
        await (await stack.Tenant.Admin.PostAsync($"/api/v1/backup-runs/{run.Id}/restore", new { target = "new", confirm = stack.ServiceName, newServiceName = "Restore check" }))
            .AssertProblemAsync(409, "service.already_exists");
    }
}
