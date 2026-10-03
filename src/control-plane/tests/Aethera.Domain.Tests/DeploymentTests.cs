namespace Aethera.Domain.Tests;

public sealed class DeploymentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static Deployment NewQueued(int number = 1, DeploymentTrigger trigger = DeploymentTrigger.Manual) =>
        Deployment.Queue(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), number, trigger, T0);

    private static Deployment NewInProgress()
    {
        var d = NewQueued();
        d.Start("""{"v":1}""", T0.AddSeconds(1));
        return d;
    }

    private static Deployment NewRunning()
    {
        var d = NewInProgress();
        d.MarkRunning(T0.AddSeconds(30));
        return d;
    }

    [Fact]
    public void Queue_StartsQueuedWithoutStepOrTimes()
    {
        var d = NewQueued(3);

        Assert.Equal(DeploymentStatus.Queued, d.Status);
        Assert.Equal(3, d.Number);
        Assert.Null(d.CurrentStep);
        Assert.Null(d.StartedAt);
        Assert.False(d.IsRollbackPoint);
        Assert.True(d.IsInProgress);
        Assert.Equal(7, d.Id.Version);
        Assert.Equal("{}", d.ConfigSnapshotJson);
    }

    [Fact]
    public void Queue_RejectsNonPositiveNumber() =>
        Assert.Throws<DomainRuleException>(() => NewQueued(0));

    [Fact]
    public void Start_FreezesConfigSnapshotAndEntersPipeline()
    {
        var d = NewQueued();

        d.Start("""{"runtime":{}}""", T0.AddSeconds(5));

        Assert.Equal(DeploymentStatus.InProgress, d.Status);
        Assert.Equal("""{"runtime":{}}""", d.ConfigSnapshotJson);
        Assert.Equal(T0.AddSeconds(5), d.StartedAt);
    }

    [Fact]
    public void HappyPath_WalksAllNineStepsAndBecomesRollbackPoint()
    {
        var d = NewInProgress();
        var t = T0.AddSeconds(2);

        foreach (var step in Enum.GetValues<DeploymentStep>().Where(s => s != DeploymentStep.Running))
        {
            d.BeginStep(step, t);
            Assert.Equal(step, d.CurrentStep);
            t = t.AddSeconds(1);
            d.CompleteStep(step, t);
        }
        d.MarkRunning(t);

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Equal(9, d.Steps.Count);
        Assert.All(d.Steps, s => Assert.Equal(StepStatus.Succeeded, s.Status));
        Assert.True(d.IsRollbackPoint);
        Assert.True(d.CanRollbackTo);
        Assert.Equal(t, d.FinishedAt);
        Assert.Equal(DeploymentStep.Running, d.CurrentStep);
        Assert.False(d.IsInProgress);
    }

    [Fact]
    public void SkipStep_RecordsSkippedRow()
    {
        var d = NewInProgress();
        d.SkipStep(DeploymentStep.Build, T0);

        var step = Assert.Single(d.Steps);
        Assert.Equal(DeploymentStep.Build, step.Step);
        Assert.Equal(StepStatus.Skipped, step.Status);
    }

    [Fact]
    public void CompleteStep_RequiresRunningStep()
    {
        var d = NewInProgress();
        Assert.Throws<DomainRuleException>(() => d.CompleteStep(DeploymentStep.Source, T0));
    }

    [Fact]
    public void MarkFailed_RecordsStepCodeReasonAndFailsTheStepRow()
    {
        var d = NewInProgress();
        d.BeginStep(DeploymentStep.Build, T0.AddSeconds(2));

        d.MarkFailed(DeploymentStep.Build, "build.failed", "npm ci exited with code 1", T0.AddSeconds(20));

        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal(DeploymentStep.Build, d.FailedStep);
        Assert.Equal("build.failed", d.FailureCode);
        Assert.Equal("npm ci exited with code 1", d.FailureReason);
        Assert.Equal(T0.AddSeconds(20), d.FinishedAt);
        var row = Assert.Single(d.Steps);
        Assert.Equal(StepStatus.Failed, row.Status);
        Assert.Equal("build.failed", row.ErrorCode);
        Assert.False(d.IsRollbackPoint);
        Assert.False(d.CanRollbackTo);
        Assert.True(d.IsTerminal);
    }

    [Fact]
    public void MarkFailed_TruncatesVeryLongReasons()
    {
        var d = NewInProgress();
        d.MarkFailed(DeploymentStep.Build, "build.failed", new string('x', Deployment.MaxFailureReasonLength + 500), T0);
        Assert.Equal(Deployment.MaxFailureReasonLength, d.FailureReason!.Length);
    }

    [Fact]
    public void MarkFailed_RequiresCodeAndReason()
    {
        Assert.Throws<ArgumentException>(() => NewInProgress().MarkFailed(DeploymentStep.Source, "source.auth_failed", " ", T0));
        Assert.Throws<ArgumentException>(() => NewInProgress().MarkFailed(DeploymentStep.Source, "", "boom", T0));
    }

    [Fact]
    public void QueuedDeployment_CanFailOrBeCancelledButNotRun()
    {
        var failed = NewQueued();
        failed.MarkFailed(DeploymentStep.Source, "source.unreachable", "git down", T0);
        Assert.Equal(DeploymentStatus.Failed, failed.Status);

        var cancelled = NewQueued();
        cancelled.MarkCancelled(T0);
        Assert.Equal(DeploymentStatus.Cancelled, cancelled.Status);
        Assert.Equal(T0, cancelled.FinishedAt);

        Assert.Throws<DomainRuleException>(() => NewQueued().MarkRunning(T0));
        Assert.Throws<DomainRuleException>(() => NewQueued().BeginStep(DeploymentStep.Source, T0));
    }

    [Fact]
    public void Cancel_StopsRunningStepRows()
    {
        var d = NewInProgress();
        d.BeginStep(DeploymentStep.Build, T0);

        d.MarkCancelled(T0.AddSeconds(3));

        Assert.Equal(StepStatus.Cancelled, Assert.Single(d.Steps).Status);
    }

    [Theory]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Cancelled)]
    [InlineData(DeploymentStatus.Superseded)]
    public void TerminalStates_AcceptNoFurtherTransitions(DeploymentStatus terminal)
    {
        Deployment d;
        switch (terminal)
        {
            case DeploymentStatus.Failed:
                d = NewInProgress();
                d.MarkFailed(DeploymentStep.Source, "source.ref_not_found", "no such branch", T0);
                break;
            case DeploymentStatus.Cancelled:
                d = NewQueued();
                d.MarkCancelled(T0);
                break;
            default:
                d = NewRunning();
                d.MarkSuperseded(T0.AddMinutes(1));
                break;
        }

        foreach (var next in Enum.GetValues<DeploymentStatus>())
            Assert.False(d.CanTransitionTo(next));
        Assert.Throws<DomainRuleException>(() => d.MarkRunning(T0));
        Assert.Throws<DomainRuleException>(() => d.MarkStopped(T0));
    }

    [Fact]
    public void Running_CannotBeCancelledOrFailed()
    {
        var d = NewRunning();
        Assert.Throws<DomainRuleException>(() => d.MarkCancelled(T0));
        Assert.Throws<DomainRuleException>(() => d.MarkFailed(DeploymentStep.Container, "container.oom", "crashed", T0));
    }

    [Fact]
    public void Running_CanBeSupersededAndStaysRollbackable()
    {
        var d = NewRunning();

        d.MarkSuperseded(T0.AddMinutes(10));

        Assert.Equal(DeploymentStatus.Superseded, d.Status);
        Assert.True(d.CanRollbackTo);
        Assert.Equal(T0.AddSeconds(30), d.FinishedAt);
    }

    [Fact]
    public void StoppedDeployment_CanResumeRunningWithoutResettingFinishTime()
    {
        var d = NewRunning();
        d.MarkStopped(T0.AddMinutes(1));
        Assert.Equal(DeploymentStatus.Stopped, d.Status);
        Assert.True(d.CanRollbackTo);

        d.MarkRunning(T0.AddMinutes(2));

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Equal(T0.AddSeconds(30), d.FinishedAt);
    }

    [Fact]
    public void StoppedDeployment_CanBeSuperseded()
    {
        var d = NewRunning();
        d.MarkStopped(T0.AddMinutes(1));
        d.MarkSuperseded(T0.AddMinutes(2));
        Assert.Equal(DeploymentStatus.Superseded, d.Status);
    }

    [Fact]
    public void RollbackDeployment_ReferencesTheRestoredOne()
    {
        var original = NewRunning();
        var rollback = Deployment.Queue(original.WorkloadId, original.EnvironmentId, original.ServerId, 2, DeploymentTrigger.Rollback, T0);
        rollback.RollbackOfDeploymentId = original.Id;
        Assert.Equal(DeploymentTrigger.Rollback, rollback.Trigger);
        Assert.Equal(original.Id, rollback.RollbackOfDeploymentId);
    }

    [Fact]
    public void WorkloadAllocatesSequentialDeploymentNumbers()
    {
        var app = new Application { Name = "API", Slug = "api" };
        Assert.Equal(1, app.AllocateDeploymentNumber());
        Assert.Equal(2, app.AllocateDeploymentNumber());
        Assert.Equal(2, app.DeploymentSequence);
    }
}
