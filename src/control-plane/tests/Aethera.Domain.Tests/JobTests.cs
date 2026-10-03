namespace Aethera.Domain.Tests;

public sealed class JobTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private static Job NewJob(int maxAttempts = 3) => new() { Type = "application.deploy", MaxAttempts = maxAttempts, RunAfter = T0 };

    private static JobError Transient => new("agent.unavailable", "Agent unavailable", Retryable: true);
    private static JobError Permanent => new("build.failed", "Build failed", "exit code 1", "build");

    [Fact]
    public void Claim_MovesToRunningWithLease()
    {
        var job = NewJob();

        job.Claim("worker-1", T0, Lease);

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Equal("worker-1", job.LockedBy);
        Assert.Equal(T0, job.StartedAt);
        Assert.Equal(T0 + Lease, job.LeaseExpiresAt);
    }

    [Fact]
    public void Claim_RejectsNonQueuedJob()
    {
        var job = NewJob();
        job.Claim("w", T0, Lease);
        Assert.Throws<DomainRuleException>(() => job.Claim("w2", T0, Lease));
    }

    [Fact]
    public void ExtendLease_RequiresRunningJob()
    {
        var job = NewJob();
        Assert.Throws<DomainRuleException>(() => job.ExtendLease(T0, Lease));
        job.Claim("w", T0, Lease);
        job.ExtendLease(T0.AddSeconds(10), Lease);
        Assert.Equal(T0.AddSeconds(70), job.LeaseExpiresAt);
    }

    [Fact]
    public void Succeed_FinishesJobAndReleasesLease()
    {
        var job = NewJob();
        job.Claim("w", T0, Lease);
        job.Succeed(T0.AddSeconds(3), """{"deploymentId":"x"}""");

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(T0.AddSeconds(3), job.FinishedAt);
        Assert.Equal("""{"deploymentId":"x"}""", job.ResultJson);
        Assert.Null(job.LockedBy);
        Assert.Null(job.LeaseExpiresAt);
        Assert.True(job.IsTerminal);
    }

    [Fact]
    public void Fail_RetryableRequeuesWithBackoffAndBumpsRetryNo()
    {
        var job = NewJob(maxAttempts: 2);
        job.Claim("w", T0, Lease);

        var willRetry = job.Fail(Transient, T0.AddSeconds(5), TimeSpan.FromSeconds(30));

        Assert.True(willRetry);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(T0.AddSeconds(35), job.RunAfter);
        Assert.Equal(1, job.RetryNo);
        Assert.Contains("agent.unavailable", job.ErrorJson);
        Assert.Null(job.LockedBy);
        Assert.Null(job.FinishedAt);
    }

    [Fact]
    public void Fail_NonRetryableErrorIsTerminalEvenWithAttemptsLeft()
    {
        var job = NewJob(maxAttempts: 3);
        job.Claim("w", T0, Lease);

        Assert.False(job.Fail(Permanent, T0, TimeSpan.FromSeconds(1)));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(0, job.RetryNo);
        Assert.Contains("\"failedStep\":\"build\"", job.ErrorJson);
    }

    [Fact]
    public void Fail_IsTerminalWhenAttemptsExhausted()
    {
        var job = NewJob(maxAttempts: 1);
        job.Claim("w", T0, Lease);

        Assert.False(job.Fail(Transient, T0, TimeSpan.FromSeconds(1)));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(T0, job.FinishedAt);
    }

    [Fact]
    public void RequestCancel_OnQueuedJob_CancelsImmediately()
    {
        var job = NewJob();
        job.RequestCancel(T0);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(T0, job.FinishedAt);
        Assert.True(job.CancelRequested);
    }

    [Fact]
    public void RequestCancel_OnRunningJob_OnlyFlagsIt()
    {
        var job = NewJob();
        job.Claim("w", T0, Lease);

        job.RequestCancel(T0);

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.True(job.CancelRequested);

        job.MarkCancelled(T0.AddSeconds(1));
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void Fail_AfterCancelRequest_DoesNotRetry()
    {
        var job = NewJob();
        job.Claim("w", T0, Lease);
        job.RequestCancel(T0);

        Assert.False(job.Fail(Transient, T0, TimeSpan.FromSeconds(1)));
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void RequestCancel_OnFinishedJob_Throws()
    {
        var job = NewJob();
        job.Claim("w", T0, Lease);
        job.Succeed(T0);
        Assert.Throws<DomainRuleException>(() => job.RequestCancel(T0));
    }

    [Fact]
    public void ExpireLease_RequeuesResumableJobWithoutConsumingAnAttempt()
    {
        var job = NewJob(maxAttempts: 3);
        job.Claim("dead-worker", T0, Lease);

        var requeued = job.ExpireLease(T0.AddSeconds(61), resumable: true);

        Assert.True(requeued);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Null(job.LockedBy);
        Assert.Equal(T0.AddSeconds(61), job.RunAfter);
    }

    [Fact]
    public void ExpireLease_FailsNonResumableJobWithWorkerLost()
    {
        var job = NewJob();
        job.Claim("dead-worker", T0, Lease);

        Assert.False(job.ExpireLease(T0.AddSeconds(61), resumable: false));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains(Job.WorkerLostCode, job.ErrorJson);
    }

    [Fact]
    public void ExpireLease_RejectsALeaseThatIsStillValid()
    {
        var job = NewJob();
        job.Claim("w", T0, Lease);
        Assert.Throws<DomainRuleException>(() => job.ExpireLease(T0.AddSeconds(10), resumable: true));
    }
}

public sealed class BuildTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Finish_ComputesDuration()
    {
        var build = new Build { Engine = BuildEngines.Dockerfile };
        build.Start(T0);

        build.Finish(BuildStatus.Succeeded, T0.AddSeconds(42.5));

        Assert.Equal(BuildStatus.Succeeded, build.Status);
        Assert.Equal(42500, build.DurationMs);
    }

    [Fact]
    public void Finish_CannotRunTwiceOrWithRunningStatus()
    {
        var build = new Build { Engine = BuildEngines.Nixpacks };
        build.Start(T0);
        Assert.Throws<DomainRuleException>(() => build.Finish(BuildStatus.Running, T0));
        build.Finish(BuildStatus.Failed, T0);
        Assert.Throws<DomainRuleException>(() => build.Finish(BuildStatus.Succeeded, T0));
    }
}
