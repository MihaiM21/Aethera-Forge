using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aethera.Api.Features.Backups;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Crypto;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Backups;

/// <summary>The <c>backup.run</c> job against a fake agent: success, failure, retry, notification, retention, the control plane's own database.</summary>
[Collection(BackupsCollection.Name)]
public sealed class BackupExecutionTests(BackupsFixture fixture)
{
    private FakeServerTransport Transport => fixture.Transport;

    private static MasterKeyring TestKeyring() => new(new Dictionary<int, byte[]> { [1] = MasterKeyring.TestKey }, 1);

    private void Reset(string container = "db-1234")
    {
        fixture.Store.Reset();
        fixture.Notifier.Reset();
        Transport.Healthy(container);
    }

    [RequiresDatabaseFact]
    public async Task Back_up_now_dumps_on_the_agent_records_the_outcome_and_tells_the_notifier()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();

        var run = await stack.BackUpAsync(policyId);

        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Equal(BackupTrigger.Manual, run.Trigger);
        Assert.Equal(BackupKind.Database, run.Kind);
        Assert.Equal("postgres", run.Engine);
        Assert.Equal(1000, run.RawSizeBytes);
        Assert.Equal(1200, run.StoredSizeBytes);
        Assert.Equal(new string('a', 64), run.StoredSha256);
        Assert.NotNull(run.StartedAt);
        Assert.NotNull(run.FinishedAt);
        Assert.Null(run.Error);
        Assert.Matches(@"^mk1-[0-9a-f]{8}$", run.KeyId);
        Assert.Equal(BackupKeys.Current(TestKeyring()).KeyId, run.KeyId);
        Assert.Matches(@"^aethera/[0-9a-f]{8}/[a-z0-9-]+-[0-9a-f]{8}/database/\d{8}T\d{6}Z-[0-9a-f]{8}\.pgdump\.aeb$", run.ObjectKey);

        var command = Assert.Single(Transport.Received<BackupRunCommand>());
        Assert.Equal(run.Id.ToString("D"), command.BackupId);
        var dump = Assert.IsType<ExecDumpSource>(command.Source);
        Assert.Equal("db-1234", dump.Container);
        Assert.Equal("pg_dump", dump.Argv[0]);
        var password = Assert.Single(dump.Env!, e => e.Name == "PGPASSWORD");
        Assert.Null(password.Plain);
        Assert.True(password.Secret!.Value.Length >= 16, "the generated service password travels as a secret variable");
        Assert.DoesNotContain(dump.Argv, a => a.Contains(password.Secret.Value));
        var target = Assert.IsType<S3LocationSpec>(command.Target);
        Assert.Equal("turn-one-backups", target.Bucket);
        Assert.Equal("https://acct123.r2.cloudflarestorage.com", target.Endpoint);
        Assert.Equal(run.ObjectKey, target.Key);
        Assert.Equal(BackupStack.Secret, target.SecretAccessKey.Value);
        Assert.Equal(run.KeyId, command.Encryption.KeyId);
        Assert.Equal(32, Convert.FromBase64String(command.Encryption.Key.Value).Length);
        Assert.Equal(BackupKeys.Current(TestKeyring()).Key, Convert.FromBase64String(command.Encryption.Key.Value));

        var ok = Assert.Single(fixture.Notifier.Events, e => e.Type == NotificationEvents.BackupSucceeded);
        Assert.Equal($"backup:{policyId}", ok.AlertKey);
        Assert.True(ok.Resolves);
        Assert.Equal(stack.Tenant.Identity.OrganizationId, ok.OrganizationId);
        Assert.Contains(stack.ServiceId, ok.Path);
        Assert.DoesNotContain(BackupStack.Secret, string.Join(' ', fixture.Notifier.Events.Select(e => e.Message + e.Title)));

        var policy = (await stack.Tenant.Viewer.GetJsonAsync($"/api/v1/backup-policies/{policyId}"));
        Assert.Equal("succeeded", policy["lastStatus"]!.GetValue<string>());
        Assert.NotNull(policy["lastRunAt"]);
        Assert.NotEmpty(await stack.Tenant.AuditAsync(policyId, "backup_policy.run_requested"));

        // and the history shows it
        var list = await stack.Tenant.Viewer.GetJsonAsync($"/api/v1/backup-runs?workloadId={stack.ServiceId}&status=succeeded");
        var item = Assert.Single(list["items"]!.AsArray())!;
        Assert.Equal(run.Id.ToString(), item.Id());
        Assert.True(item["restorable"]!.GetValue<bool>());
        Assert.True(item["durationMs"]!.GetValue<long>() >= 0);
    }

    [RequiresDatabaseFact]
    public async Task The_job_log_narrates_the_steps_and_never_contains_secrets()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        var run = await stack.BackUpAsync(policyId);

        var text = string.Join('\n', (await stack.WithDbAsync(db => db.LogChunks.AsNoTracking().Where(c => c.StreamId == $"job:{run.JobId}").OrderBy(c => c.Sequence).ToListAsync()))
            .Select(c => c.Data));
        Assert.Contains("Backing up", text);
        Assert.Contains("Dumping the postgres database", text);
        Assert.Contains("Backup stored as", text);
        Assert.DoesNotContain(BackupStack.Secret, text);
    }

    [RequiresDatabaseFact]
    public async Task A_failed_dump_fails_the_run_and_the_job_and_raises_an_alert_event()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        Transport.Fail<BackupRunCommand, BackupRunOutcome>(CommandErrorCode.Internal, "pg_dump: error: connection to server failed");

        var jobId = await stack.RunNowAsync(policyId);
        var job = await stack.WaitForJobAsync(jobId);
        var run = (await stack.RunsAsync(policyId)).Single();

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("backup.agent_failed", job.ErrorJson);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Contains("connection to server failed", run.Error);
        Assert.Null(run.StoredSha256);
        var failed = Assert.Single(fixture.Notifier.Events, e => e.Type == NotificationEvents.BackupFailed);
        Assert.Equal($"backup:{policyId}", failed.AlertKey);
        Assert.False(failed.Resolves);
        Assert.Contains("connection to server failed", failed.Message);
        Assert.Equal("failed", (await stack.Tenant.Viewer.GetJsonAsync($"/api/v1/backup-policies/{policyId}"))["lastStatus"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task An_agent_without_the_backup_capability_is_told_to_update()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        Transport.Throw<BackupRunCommand>(new ServerTransportException(TransportErrors.Unsupported, "The agent on this server does not support 'backup.v1'. Update the agent."));

        var job = await stack.WaitForJobAsync(await stack.RunNowAsync(policyId));
        var run = (await stack.RunsAsync(policyId)).Single();

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempt); // not worth retrying
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Contains("Update the agent", run.Error);
        Assert.Contains("backup.v1", run.Error);
        Assert.Single(fixture.Notifier.Events, e => e.Type == NotificationEvents.BackupFailed);
    }

    [RequiresDatabaseFact]
    public async Task An_unreachable_agent_is_retried_and_only_the_final_failure_alerts()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        Transport.Throw<BackupRunCommand>(new ServerTransportException(TransportErrors.AgentUnavailable, "No agent session is connected for this server."));

        var job = await stack.WaitForJobAsync(await stack.RunNowAsync(policyId));
        var run = (await stack.RunsAsync(policyId)).Single();

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempt);
        Assert.Equal(3, Transport.Received<BackupRunCommand>().Count);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Single(fixture.Notifier.Events, e => e.Type == NotificationEvents.BackupFailed);
    }

    [RequiresDatabaseFact]
    public async Task A_stopped_database_is_not_dumped()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        Transport.On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => []);

        var job = await stack.WaitForJobAsync(await stack.RunNowAsync(policyId));
        var run = (await stack.RunsAsync(policyId)).Single();

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("no running container", run.Error);
        Assert.Empty(Transport.Received<BackupRunCommand>());
    }

    [RequiresDatabaseFact]
    public async Task A_volume_policy_archives_the_named_volume()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var volumeId = (await stack.Tenant.Viewer.GetJsonAsync(stack.PoliciesUrl))["targets"]!.AsArray().Single(x => x!["kind"]!.GetValue<string>() == "volume")!["volumeId"]!.GetValue<string>();
        var policyId = (await stack.PutPolicyAsync(new { kind = "volume", volumeId, destinationId = stack.DestinationId }))["id"]!.GetValue<string>();

        var run = await stack.BackUpAsync(policyId);

        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Equal("volume", run.Engine);
        Assert.EndsWith(".tar.gz.aeb", run.ObjectKey);
        Assert.Contains("/volume/", run.ObjectKey);
        var archive = Assert.IsType<VolumeArchiveSource>(Assert.Single(Transport.Received<BackupRunCommand>()).Source);
        var volume = await stack.WithDbAsync(db => db.Volumes.AsNoTracking().FirstAsync(v => v.Id == Guid.Parse(volumeId)));
        Assert.Equal(volume.Name, archive.Volume);
        Assert.Empty(Transport.Received<ContainerListCommand>()); // an archive needs no running container
    }

    [RequiresDatabaseFact]
    public async Task Back_up_now_needs_a_developer_and_the_deploy_scope()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        var url = $"/api/v1/backup-policies/{policyId}/run";

        await (await stack.Tenant.Viewer.PostAsync(url, new { })).AssertProblemAsync(403, "auth.forbidden");
        await (await stack.Tenant.Token(OrganizationRole.Developer, "write").PostAsync(url, new { })).AssertProblemAsync(403, "auth.insufficient_scope");
        var accepted = await stack.Tenant.Token(OrganizationRole.Developer, "deploy").PostAsync(url, new { });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await stack.WaitForJobAsync((await accepted.ReadAsync()).Id()); // let it finish before the next test scripts the fake agent
    }

    // ---- retention ----------------------------------------------------------------------------------------------------------------

    private static BackupRun OldRun(string policyId, Guid destinationId, Guid organizationId, Guid workloadId, int daysAgo, BackupRunStatus status = BackupRunStatus.Succeeded)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        return new BackupRun
        {
            OrganizationId = organizationId, PolicyId = Guid.Parse(policyId), WorkloadId = workloadId, DestinationId = destinationId, Kind = BackupKind.Database,
            Trigger = BackupTrigger.Scheduled, Status = status, ObjectKey = $"aethera/old/{daysAgo}-days.pgdump.aeb", StoredSha256 = new string('b', 64), KeyId = "mk1-00000000",
            CreatedAt = at, StartedAt = at, FinishedAt = at,
        };
    }

    [RequiresDatabaseFact]
    public async Task After_a_backup_retention_removes_old_objects_but_never_the_newest_and_ignores_failed_runs()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync(new { kind = "database", destinationId = stack.DestinationId, keepDaily = 2, keepWeekly = 0, keepMonthly = 0 });
        var org = stack.Tenant.Identity.OrganizationId;
        var seeded = new[] { 5, 4, 3, 2 }.Select(d => OldRun(policyId, Guid.Parse(stack.DestinationId), org, Guid.Parse(stack.ServiceId), d)).ToList();
        var failed = OldRun(policyId, Guid.Parse(stack.DestinationId), org, Guid.Parse(stack.ServiceId), 6, BackupRunStatus.Failed);
        failed.ObjectKey = null;
        await stack.WithDbAsync(async db =>
        {
            db.BackupRuns.AddRange(seeded);
            db.BackupRuns.Add(failed);
            return await db.SaveChangesAsync();
        });

        var run = await stack.BackUpAsync(policyId);
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);

        var runs = (await stack.RunsAsync(policyId)).ToDictionary(r => r.Id);
        var pruned = seeded.Where(s => runs[s.Id].PrunedAt is not null).Select(s => s.ObjectKey!).Order().ToList();
        Assert.Equal(new[] { "aethera/old/3-days.pgdump.aeb", "aethera/old/4-days.pgdump.aeb", "aethera/old/5-days.pgdump.aeb" }, pruned);
        Assert.Null(runs[seeded.Single(s => s.ObjectKey!.Contains("/2-days")).Id].PrunedAt); // the newest older day survives with today
        Assert.Null(runs[run.Id].PrunedAt);
        Assert.Null(runs[failed.Id].PrunedAt);
        Assert.Equal(pruned, fixture.Store.Deleted.Order().ToList());
        Assert.Empty(Transport.Received<BackupDeleteCommand>()); // S3 objects are deleted by the control plane itself

        // a pruned backup is history, not a restore point
        var pruneRun = seeded.First(s => s.ObjectKey!.Contains("/5-days"));
        var restore = await stack.Tenant.Admin.PostAsync($"/api/v1/backup-runs/{pruneRun.Id}/restore", new { target = "same", confirm = stack.ServiceName });
        await restore.AssertProblemAsync(409, "backup_run.not_restorable");
    }

    [RequiresDatabaseFact]
    public async Task Retention_of_a_local_destination_deletes_through_the_agent()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var local = await stack.Tenant.Admin.CreateAsync("/api/v1/backup-destinations", new { name = Tenant.Unique("local"), kind = "local" });
        var policyId = await stack.CreateDatabasePolicyAsync(new { kind = "database", destinationId = local.Id(), keepDaily = 1, keepWeekly = 0, keepMonthly = 0 });
        var old = OldRun(policyId, Guid.Parse(local.Id()), stack.Tenant.Identity.OrganizationId, Guid.Parse(stack.ServiceId), 3);
        await stack.WithDbAsync(async db => { db.BackupRuns.Add(old); return await db.SaveChangesAsync(); });

        var run = await stack.BackUpAsync(policyId);

        Assert.IsType<LocalBackupLocationSpec>(Assert.Single(Transport.Received<BackupRunCommand>()).Target);
        Assert.Equal(run.ObjectKey, ((LocalBackupLocationSpec)Transport.Received<BackupRunCommand>()[0].Target).Path);
        Assert.DoesNotContain("aethera/", run.ObjectKey); // no bucket prefix for a local destination
        var delete = Assert.Single(Transport.Received<BackupDeleteCommand>());
        Assert.Equal(old.ObjectKey, Assert.IsType<LocalBackupLocationSpec>(Assert.Single(delete.Locations)).Path);
        Assert.Empty(fixture.Store.Deleted);
        Assert.NotNull((await stack.RunsAsync(policyId)).Single(r => r.Id == old.Id).PrunedAt);
    }

    // ---- Aethera's own database ---------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task The_control_plane_database_is_encrypted_here_and_uploaded_and_can_be_decrypted_with_the_master_key()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var t = stack.Tenant;
        var policy = (await (await t.Admin.PutAsync("/api/v1/system/backup-policy", Json.Body(new { destinationId = stack.DestinationId }))).ReadAsync())["policy"]!;
        var policyId = policy.Id();

        await (await t.Developer.PostAsync($"/api/v1/backup-policies/{policyId}/run", new { })).AssertProblemAsync(403, "auth.forbidden");
        var run = await stack.BackUpAsync2(policyId, t.Admin);

        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Equal(BackupKind.ControlPlane, run.Kind);
        Assert.Equal("aethera", run.Engine);
        Assert.Null(run.WorkloadId);
        Assert.Matches(@"^aethera/[0-9a-f]{8}/aethera/controlplane/\d{8}T\d{6}Z-[0-9a-f]{8}\.pgdump\.aeb$", run.ObjectKey);
        Assert.Empty(Transport.Received<BackupRunCommand>()); // the control plane does it itself, the agent is not involved

        var uploaded = Assert.Single(fixture.Store.Uploaded);
        Assert.Equal(run.ObjectKey, uploaded.Key);
        Assert.Equal(run.StoredSizeBytes, uploaded.Value.Length);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(uploaded.Value)), run.StoredSha256);
        Assert.Equal(FakeControlPlaneDump.Content.Length, run.RawSizeBytes);
        Assert.NotEqual(FakeControlPlaneDump.Content, uploaded.Value[..FakeControlPlaneDump.Content.Length]); // really encrypted

        // the same way the host tool reads it: the key id in the header names the master key version
        var keyring = TestKeyring();
        using var plain = new MemoryStream();
        await Aeb1Decryptor.DecryptAsync(new MemoryStream(uploaded.Value), plain, id => BackupKeys.TryForKeyId(keyring, id, out var key) ? key!.Key : null);
        Assert.Equal(FakeControlPlaneDump.Content, plain.ToArray());

        // only administrators see these runs, and the API does not restore them
        Assert.Contains(((await t.Admin.GetJsonAsync("/api/v1/backup-runs"))["items"]!.AsArray()), r => r!.Id() == run.Id.ToString());
        Assert.DoesNotContain(((await t.Viewer.GetJsonAsync("/api/v1/backup-runs"))["items"]!.AsArray()), r => r!.Id() == run.Id.ToString());
        await (await t.Viewer.GetAsync($"/api/v1/backup-runs/{run.Id}")).AssertProblemAsync(404, "backup_run.not_found");
        var restore = await t.Admin.PostAsync($"/api/v1/backup-runs/{run.Id}/restore", new { target = "same", confirm = "x" });
        await restore.AssertProblemAsync(409, "backup_run.not_restorable");
        Assert.Contains(fixture.Notifier.Events, e => e.Type == NotificationEvents.BackupSucceeded && e.AlertKey == $"backup:{policyId}");
    }

    [Fact]
    public async Task The_host_tool_decrypts_with_the_configured_master_key_and_refuses_a_wrong_one()
    {
        var key = BackupKeys.Current(TestKeyring());
        var plain = Enumerable.Range(0, 70000).Select(i => (byte)(i % 199)).ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "aethera-decrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "in.aeb");
        var output = Path.Combine(directory, "out.dump");
        await using (var file = File.Create(input)) await Aeb1Encryptor.EncryptAsync(new MemoryStream(plain), file, key.Key, key.KeyId);

        var previous = Environment.GetEnvironmentVariable("AETHERA_MASTER_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AETHERA_MASTER_KEY", Convert.ToBase64String(MasterKeyring.TestKey));
            var error = new StringWriter();
            Assert.Equal(0, await BackupDecryptCommand.RunAsync([input, output], error));
            Assert.Equal(plain, await File.ReadAllBytesAsync(output));

            File.Delete(output);
            Environment.SetEnvironmentVariable("AETHERA_MASTER_KEY", Convert.ToBase64String(new byte[32]));
            var wrong = new StringWriter();
            Assert.Equal(1, await BackupDecryptCommand.RunAsync([input, output], wrong));
            Assert.Contains("No key is available", wrong.ToString());
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));
            Assert.Equal(2, await BackupDecryptCommand.RunAsync([input], new StringWriter()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AETHERA_MASTER_KEY", previous);
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    // ---- runs API ---------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Runs_are_listed_filtered_and_isolated_between_organizations()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        var good = await stack.BackUpAsync(policyId);
        Transport.Fail<BackupRunCommand, BackupRunOutcome>(CommandErrorCode.Internal, "boom");
        var bad = await stack.BackUpAsync(policyId);
        var t = stack.Tenant;

        var all = (await t.Viewer.GetJsonAsync($"/api/v1/backup-runs?policyId={policyId}"))["items"]!.AsArray();
        Assert.Equal(2, all.Count);
        Assert.Equal(bad.Id.ToString(), all[0]!.Id()); // newest first
        Assert.False(all[0]!["restorable"]!.GetValue<bool>());
        Assert.Equal("boom", all[0]!["error"]!.GetValue<string>());
        var failed = (await t.Viewer.GetJsonAsync($"/api/v1/backup-runs?policyId={policyId}&status=failed"))["items"]!.AsArray();
        Assert.Equal(bad.Id.ToString(), Assert.Single(failed)!.Id());
        Assert.Equal(good.Id.ToString(), (await t.Viewer.GetJsonAsync($"/api/v1/backup-runs/{good.Id}")).Id());
        var page = await t.Viewer.GetJsonAsync($"/api/v1/backup-runs?policyId={policyId}&limit=1");
        Assert.Single(page["items"]!.AsArray());
        Assert.NotNull(page["nextCursor"]);
        await (await t.Viewer.GetAsync("/api/v1/backup-runs?status=nonsense")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await t.Viewer.GetAsync("/api/v1/backup-runs?bogus=1")).AssertProblemAsync(400, "validation.invalid_parameter");

        var other = await fixture.NewTenantAsync();
        Assert.Empty((await other.Admin.GetJsonAsync("/api/v1/backup-runs"))["items"]!.AsArray());
        await (await other.Admin.GetAsync($"/api/v1/backup-runs/{good.Id}")).AssertProblemAsync(404, "backup_run.not_found");
        await (await other.Admin.PostAsync($"/api/v1/backup-runs/{good.Id}/restore", new { target = "same", confirm = stack.ServiceName })).AssertProblemAsync(404, "backup_run.not_found");
    }

    // ---- the scheduler ----------------------------------------------------------------------------------------------------------

    private async Task MakeDueAsync(BackupStack stack, string policyId) =>
        await stack.WithDbAsync(async db =>
        {
            var policy = await db.BackupPolicies.FirstAsync(p => p.Id == Guid.Parse(policyId));
            policy.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            return await db.SaveChangesAsync();
        });

    private BackupScheduler Scheduler => fixture.Factory.Services.GetRequiredService<BackupScheduler>();

    [RequiresDatabaseFact]
    public async Task The_scheduler_queues_a_due_policy_once_and_moves_the_next_run_forward()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        await MakeDueAsync(stack, policyId);

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await Scheduler.RunOnceAsync(now, CancellationToken.None));
        Assert.Equal(0, await Scheduler.RunOnceAsync(now, CancellationToken.None));

        var runs = await stack.RunsAsync(policyId);
        var run = Assert.Single(runs);
        Assert.Equal(BackupTrigger.Scheduled, run.Trigger);
        var policy = await stack.WithDbAsync(db => db.BackupPolicies.AsNoTracking().FirstAsync(p => p.Id == Guid.Parse(policyId)));
        Assert.True(policy.NextRunAt > now);
        Assert.Equal(3, policy.NextRunAt!.Value.Hour); // 03:00 UTC, the schedule's next occurrence

        // the job it queued carries the workload's lock key, so it never overlaps a deployment of the same workload
        var job = await stack.WaitForJobAsync(await WaitJobIdAsync(stack, run.Id));
        Assert.Equal(DeploymentLockKey(stack.ServiceId), job.LockKey);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(BackupJobs.RunType, job.Type);
    }

    private static string DeploymentLockKey(string workloadId) => $"app:{workloadId}";

    private static async Task<string> WaitJobIdAsync(BackupStack stack, Guid runId)
    {
        Guid? jobId = null;
        await GatewayFixture.EventuallyAsync(async () =>
        {
            jobId = await stack.WithDbAsync(db => db.BackupRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.JobId).FirstAsync());
            return jobId is not null;
        }, "the worker to pick the backup up", 20000);
        return jobId!.Value.ToString();
    }

    [RequiresDatabaseFact]
    public async Task Two_scheduler_instances_never_queue_the_same_backup_twice()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        await MakeDueAsync(stack, policyId);

        var now = DateTimeOffset.UtcNow;
        var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Scheduler.RunOnceAsync(now, CancellationToken.None))));

        Assert.Equal(1, counts.Sum());
        var only = Assert.Single(await stack.RunsAsync(policyId));
        await stack.WaitForJobAsync(await WaitJobIdAsync(stack, only.Id)); // let the queued job finish before the next test scripts the fake agent
    }

    [RequiresDatabaseFact]
    public async Task Disabled_policies_and_policies_of_deleted_workloads_are_not_scheduled()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        await MakeDueAsync(stack, policyId);
        await stack.PutPolicyAsync(new { kind = "database", destinationId = stack.DestinationId, enabled = false });
        await stack.WithDbAsync(async db =>
        {
            (await db.BackupPolicies.FirstAsync(p => p.Id == Guid.Parse(policyId))).NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            return await db.SaveChangesAsync();
        });
        Assert.Equal(0, await Scheduler.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None));

        // a deleted workload: the policy switches itself off instead of failing every night
        var other = await BackupStack.CreateAsync(fixture);
        var otherPolicy = await other.CreateDatabasePolicyAsync();
        await MakeDueAsync(other, otherPolicy);
        await (await other.Tenant.Developer.DeleteAsync($"/api/v1/services/{other.ServiceId}?confirm={await SlugAsync(other)}")).AssertNoContentAsync();
        Assert.Equal(0, await Scheduler.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        var policy = await other.WithDbAsync(db => db.BackupPolicies.AsNoTracking().FirstAsync(p => p.Id == Guid.Parse(otherPolicy)));
        Assert.False(policy.Enabled);
        Assert.Null(policy.NextRunAt);
        Assert.Empty(await other.RunsAsync(otherPolicy));
    }

    private static async Task<string> SlugAsync(BackupStack stack) => (await stack.ServiceAsync())["slug"]!.GetValue<string>();

    [RequiresDatabaseFact]
    public async Task A_backup_still_running_is_not_started_a_second_time()
    {
        Reset();
        var stack = await BackupStack.CreateAsync(fixture);
        var policyId = await stack.CreateDatabasePolicyAsync();
        await stack.WithDbAsync(async db =>
        {
            db.BackupRuns.Add(BackupRunner.NewRun(await db.BackupPolicies.FirstAsync(p => p.Id == Guid.Parse(policyId)), Guid.CreateVersion7(), BackupTrigger.Scheduled, null));
            return await db.SaveChangesAsync();
        });
        await MakeDueAsync(stack, policyId);

        Assert.Equal(0, await Scheduler.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        var policy = await stack.WithDbAsync(db => db.BackupPolicies.AsNoTracking().FirstAsync(p => p.Id == Guid.Parse(policyId)));
        Assert.True(policy.NextRunAt > DateTimeOffset.UtcNow); // but the next occurrence was computed
    }
}

internal static class BackupAssertions
{
    public static async Task AssertNoContentAsync(this HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    /// <summary>Like <see cref="BackupStack.BackUpAsync"/> with another client (administrators back up Aethera's own database).</summary>
    public static async Task<BackupRun> BackUpAsync2(this BackupStack stack, string policyId, HttpClient client)
    {
        var job = await stack.WaitForJobAsync(await stack.RunNowAsync(policyId, client));
        return await stack.WithDbAsync(db => db.BackupRuns.AsNoTracking().FirstAsync(r => r.PolicyId == Guid.Parse(policyId) && r.JobId == job.Id));
    }
}
