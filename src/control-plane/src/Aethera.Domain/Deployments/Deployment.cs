namespace Aethera.Domain;

public enum DeploymentTrigger
{
    Manual = 0,
    Webhook = 1,
    Redeploy = 2,
    Rollback = 3,
    Schedule = 4,
    Api = 5,
}

/// <summary>
/// Coarse status of a deployment (ADR 0004). Where it failed is <see cref="Deployment.FailedStep"/>, not a status.
/// <para>Queued -> InProgress -> <b>Running</b> (the live deployment) -> later <b>Superseded</b> (replaced by a newer
/// successful one, still a rollback point) or <b>Stopped</b> (the user stopped the application).
/// Failed and Cancelled are terminal.</para>
/// </summary>
public enum DeploymentStatus
{
    Queued = 0,
    InProgress = 1,
    Running = 2,
    Superseded = 3,
    Stopped = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>The nine pipeline steps of spec section 5 (ADR 0004), in order.</summary>
public enum DeploymentStep
{
    Source = 0,
    Build = 1,
    Image = 2,
    TargetServer = 3,
    Container = 4,
    Network = 5,
    Domain = 6,
    HealthCheck = 7,
    Running = 8,
}

public enum StepStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Skipped = 3,
    Cancelled = 4,
}

/// <summary>
/// One row per step, written as the step starts and finishes. It is the checkpoint used to resume a deployment after a
/// crash and the data behind the UI stepper (table <c>deployment_steps</c>).
/// </summary>
public class DeploymentStepRun
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public DeploymentStep Step { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Running;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Step output (jsonb): e.g. container id, selected transport, last probe result.</summary>
    public string? DetailsJson { get; set; }
}

public class Deployment : MutableEntity
{
    public const int MaxFailureReasonLength = 4000;

    private Deployment() { }

    /// <summary>Creates a queued deployment. <paramref name="number"/> comes from <see cref="Workload.AllocateDeploymentNumber"/>.</summary>
    public static Deployment Queue(
        Guid workloadId,
        Guid environmentId,
        Guid serverId,
        int number,
        DeploymentTrigger trigger,
        DateTimeOffset now,
        string strategy = DeploymentStrategies.Recreate)
    {
        if (number < 1) throw new DomainRuleException("Deployment number must be positive.");
        return new Deployment
        {
            WorkloadId = workloadId, EnvironmentId = environmentId, ServerId = serverId, Number = number,
            Trigger = trigger, Strategy = strategy, Status = DeploymentStatus.Queued,
            CreatedAt = now, UpdatedAt = now,
        };
    }

    private static readonly Dictionary<DeploymentStatus, DeploymentStatus[]> Transitions = new()
    {
        [DeploymentStatus.Queued] = [DeploymentStatus.InProgress, DeploymentStatus.Failed, DeploymentStatus.Cancelled],
        [DeploymentStatus.InProgress] = [DeploymentStatus.Running, DeploymentStatus.Failed, DeploymentStatus.Cancelled],
        [DeploymentStatus.Running] = [DeploymentStatus.Superseded, DeploymentStatus.Stopped],
        [DeploymentStatus.Stopped] = [DeploymentStatus.Running, DeploymentStatus.Superseded],
        [DeploymentStatus.Superseded] = [],
        [DeploymentStatus.Failed] = [],
        [DeploymentStatus.Cancelled] = [],
    };

    public Guid WorkloadId { get; private set; }
    public Workload Workload { get; set; } = null!;

    /// <summary>Denormalised from the workload for filtering (ADR 0004 lists it on the deployment).</summary>
    public Guid EnvironmentId { get; private set; }

    public Guid ServerId { get; private set; }
    public Server Server { get; set; } = null!;

    /// <summary>Job executing this deployment; its logs are the <c>deploy:&lt;id&gt;</c> stream.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Sequential per workload (#1, #2, ...); unique together with <see cref="WorkloadId"/>.</summary>
    public int Number { get; private set; }

    public DeploymentTrigger Trigger { get; private set; }
    public DeploymentStatus Status { get; private set; }

    /// <summary>Strategy name (<see cref="DeploymentStrategies"/>) chosen for this run.</summary>
    public string Strategy { get; private set; } = DeploymentStrategies.Recreate;

    public DeploymentStep? CurrentStep { get; private set; }
    public DeploymentStep? FailedStep { get; private set; }

    /// <summary>Machine-readable code, e.g. <c>build.failed</c>, <c>health.timeout</c> (ADR 0004 step table).</summary>
    public string? FailureCode { get; private set; }
    public string? FailureReason { get; private set; }

    // Source revision
    public ApplicationSourceKind? SourceType { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? Ref { get; set; }
    public string? CommitSha { get; set; }
    public string? CommitMessage { get; set; }
    public string? CommitAuthor { get; set; }

    // Result
    public Guid? BuildId { get; set; }
    public string? ImageRef { get; set; }
    public string? ImageDigest { get; set; }

    /// <summary>Container ids (several for Compose workloads).</summary>
    public List<string> ContainerIds { get; set; } = [];

    /// <summary>
    /// Frozen at job start (not at queue time): build + runtime config, env names/plain values, secret id+version references
    /// (never values), volumes, domains, resources, health check, strategy. Used by redeploy and rollback.
    /// </summary>
    public string ConfigSnapshotJson { get; private set; } = "{}";

    /// <summary>Last health-check result (jsonb).</summary>
    public string? HealthCheckResultJson { get; set; }

    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>True once the deployment has reached Running; such deployments can be rolled back to.</summary>
    public bool IsRollbackPoint { get; private set; }

    /// <summary>For Trigger=Rollback: the earlier deployment being restored.</summary>
    public Guid? RollbackOfDeploymentId { get; set; }
    public Deployment? RollbackOfDeployment { get; set; }

    public Guid? TriggeredByUserId { get; set; }
    public Guid? TriggeredByApiTokenId { get; set; }

    public List<DeploymentStepRun> Steps { get; set; } = [];
    public List<Build> Builds { get; set; } = [];

    public bool IsTerminal => Status is DeploymentStatus.Failed or DeploymentStatus.Cancelled or DeploymentStatus.Superseded;

    /// <summary>Queued or somewhere in the pipeline.</summary>
    public bool IsInProgress => Status is DeploymentStatus.Queued or DeploymentStatus.InProgress;

    /// <summary>Can be selected as a rollback target (the image may still have been pruned; check separately).</summary>
    public bool CanRollbackTo => IsRollbackPoint && Status is DeploymentStatus.Running or DeploymentStatus.Stopped or DeploymentStatus.Superseded;

    public TimeSpan? Duration => StartedAt is { } s && FinishedAt is { } f ? f - s : null;

    public bool CanTransitionTo(DeploymentStatus next) => Transitions[Status].Contains(next);

    /// <summary>The job picked the deployment up: freeze the configuration and enter the pipeline.</summary>
    public void Start(string configSnapshotJson, DateTimeOffset now)
    {
        MoveTo(DeploymentStatus.InProgress, now);
        ConfigSnapshotJson = configSnapshotJson;
        StartedAt ??= now;
    }

    /// <summary>Enters a step (writes/updates its <c>deployment_steps</c> row, which makes resume after a crash possible).</summary>
    public DeploymentStepRun BeginStep(DeploymentStep step, DateTimeOffset now)
    {
        RequireInProgress();
        var run = Steps.FirstOrDefault(s => s.Step == step);
        if (run is null)
        {
            run = new DeploymentStepRun { DeploymentId = Id, Step = step };
            Steps.Add(run);
        }
        run.Status = StepStatus.Running;
        run.StartedAt = now;
        run.FinishedAt = null;
        run.ErrorCode = null;
        run.ErrorMessage = null;
        CurrentStep = step;
        UpdatedAt = now;
        return run;
    }

    public void CompleteStep(DeploymentStep step, DateTimeOffset now, string? detailsJson = null)
    {
        RequireInProgress();
        var run = Steps.FirstOrDefault(s => s.Step == step && s.Status == StepStatus.Running)
                  ?? throw new DomainRuleException($"Step {step} is not running.");
        run.Status = StepStatus.Succeeded;
        run.FinishedAt = now;
        run.DetailsJson = detailsJson ?? run.DetailsJson;
        UpdatedAt = now;
    }

    /// <summary>Records a step that does not apply (e.g. Build for an image source).</summary>
    public void SkipStep(DeploymentStep step, DateTimeOffset now)
    {
        RequireInProgress();
        var run = Steps.FirstOrDefault(s => s.Step == step);
        if (run is null)
        {
            run = new DeploymentStepRun { DeploymentId = Id, Step = step };
            Steps.Add(run);
        }
        run.Status = StepStatus.Skipped;
        run.StartedAt ??= now;
        run.FinishedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Promotes the deployment to live (step 9). Also resumes a Stopped deployment.</summary>
    public void MarkRunning(DateTimeOffset now)
    {
        var resuming = Status == DeploymentStatus.Stopped;
        MoveTo(DeploymentStatus.Running, now);
        if (resuming) return;

        var run = Steps.FirstOrDefault(s => s.Step == DeploymentStep.Running);
        if (run is null)
        {
            run = new DeploymentStepRun { DeploymentId = Id, Step = DeploymentStep.Running };
            Steps.Add(run);
        }
        run.Status = StepStatus.Succeeded;
        run.StartedAt ??= now;
        run.FinishedAt = now;
        CurrentStep = DeploymentStep.Running;
        FinishedAt = now;
        IsRollbackPoint = true;
    }

    /// <summary>The user stopped the application; the deployment stays a rollback point.</summary>
    public void MarkStopped(DateTimeOffset now) => MoveTo(DeploymentStatus.Stopped, now);

    /// <summary>Replaced by a newer successful deployment.</summary>
    public void MarkSuperseded(DateTimeOffset now) => MoveTo(DeploymentStatus.Superseded, now);

    /// <summary>Fails the deployment at <paramref name="step"/>; also marks that step's row failed.</summary>
    public void MarkFailed(DeploymentStep step, string code, string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        MoveTo(DeploymentStatus.Failed, now);
        var trimmed = reason.Length > MaxFailureReasonLength ? reason[..MaxFailureReasonLength] : reason;

        var run = Steps.FirstOrDefault(s => s.Step == step);
        if (run is null)
        {
            run = new DeploymentStepRun { DeploymentId = Id, Step = step, StartedAt = now };
            Steps.Add(run);
        }
        run.Status = StepStatus.Failed;
        run.FinishedAt = now;
        run.ErrorCode = code;
        run.ErrorMessage = trimmed;

        FailedStep = step;
        CurrentStep = step;
        FailureCode = code;
        FailureReason = trimmed;
        FinishedAt = now;
    }

    public void MarkCancelled(DateTimeOffset now)
    {
        MoveTo(DeploymentStatus.Cancelled, now);
        foreach (var run in Steps.Where(s => s.Status == StepStatus.Running))
        {
            run.Status = StepStatus.Cancelled;
            run.FinishedAt = now;
        }
        FinishedAt = now;
    }

    private void RequireInProgress()
    {
        if (Status != DeploymentStatus.InProgress)
            throw new DomainRuleException($"Steps can only change while the deployment is InProgress (is {Status}).");
    }

    private void MoveTo(DeploymentStatus next, DateTimeOffset now)
    {
        if (!CanTransitionTo(next))
            throw new DomainRuleException($"Deployment cannot move from {Status} to {next}.");
        Status = next;
        UpdatedAt = now;
    }
}
