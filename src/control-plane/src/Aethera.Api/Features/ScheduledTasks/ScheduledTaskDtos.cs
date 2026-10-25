using System.Text.RegularExpressions;
using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Scheduling;
using FluentValidation;
using FluentValidation.Results;

namespace Aethera.Api.Features.ScheduledTasks;

public sealed record CreateScheduledTaskRequest
{
    /// <summary>Unique among the live tasks of the application or service. 1 to 200 characters.</summary>
    public string? Name { get; init; }

    /// <summary>Command line run as <c>sh -c "..."</c> inside the container. 1 to 4000 characters.</summary>
    public string? Command { get; init; }

    /// <summary>Compose applications: the service whose container runs the command (required). Absent for everything else.</summary>
    public string? ComposeService { get; init; }

    /// <summary>Five-field cron expression, evaluated in <see cref="TimeZone"/>. At most once per minute.</summary>
    public string? Schedule { get; init; }

    /// <summary>IANA time zone (<c>Europe/Bucharest</c>). Default <c>UTC</c>.</summary>
    public string? TimeZone { get; init; }

    /// <summary>Longest a run may take, 1 s to 24 h (86400). Default one hour.</summary>
    public int? TimeoutSeconds { get; init; }

    public bool? Enabled { get; init; }
}

public sealed record UpdateScheduledTaskRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public string? Command { get; init; }
    public string? ComposeService { get; init; }
    [NotClearable] public string? Schedule { get; init; }
    [NotClearable] public string? TimeZone { get; init; }
    [NotClearable] public int? TimeoutSeconds { get; init; }
    [NotClearable] public bool? Enabled { get; init; }
}

/// <summary>How the last run of a task went (the full history is <c>GET /scheduled-tasks/{id}/runs</c>).</summary>
public sealed record ScheduledTaskRunSummary(
    Guid Id, ScheduledTaskTrigger Trigger, ScheduledTaskRunStatus Status, int? ExitCode, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, long? DurationMs, Guid? JobId);

public sealed record ScheduledTaskResponse(
    Guid Id, Guid WorkloadId, string WorkloadKind, string WorkloadName, string Name, string Command, string? ComposeService, string Schedule,
    string TimeZone, int TimeoutSeconds, bool Enabled, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt,
    IReadOnlyList<DateTimeOffset> NextRuns, ScheduledTaskRunSummary? LastRun, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="LogStreamId">The persisted log stream of the command's output (<c>task:&lt;runId&gt;</c>), readable at <c>/scheduled-task-runs/{id}/logs</c> and on <c>/hubs/logs</c>.</param>
public sealed record ScheduledTaskRunResponse(
    Guid Id, Guid TaskId, Guid WorkloadId, Guid? JobId, ScheduledTaskTrigger Trigger, ScheduledTaskRunStatus Status, int? ExitCode, string? OutputTail,
    bool OutputTruncated, string? Error, Guid? CreatedByUserId, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    long? DurationMs, string LogStreamId);

/// <summary>Answer of <c>POST /scheduled-tasks/{id}/run</c>: the run that was created and the job that executes it.</summary>
public sealed record RunScheduledTaskResponse(ScheduledTaskRunResponse Run, JobDto Job);

/// <summary>Answer of the schedule preview: the next three occurrences.</summary>
public sealed record SchedulePreviewResponse(string Schedule, string TimeZone, IReadOnlyList<DateTimeOffset> NextRuns);

/// <summary>Shared rules of scheduled tasks.</summary>
public static partial class ScheduledTaskRules
{
    public const int MaxCommandLength = 4000;
    public const int MaxNameLength = 200;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 86400;
    public const int DefaultTimeoutSeconds = 3600;

    /// <summary>The most frequent schedule allowed: once a minute.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,199}\z")]
    private static partial Regex ServicePattern();

    public static bool IsValidComposeService(string? name) => name is not null && ServicePattern().IsMatch(name);

    /// <summary>The first problem of a schedule: <c>(pointer, code, message)</c>, or null when it is valid.</summary>
    public static (string Pointer, string Code, string Message)? CheckSchedule(string? schedule, string? timeZone, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(timeZone) && !CronSchedule.TryParse("* * * * *", timeZone, out _, out _))
            return ("/timeZone", "time_zone.unknown", $"Unknown time zone '{timeZone}'. Use an IANA name such as Europe/Bucharest.");
        if (!CronSchedule.TryParse(schedule, timeZone, out var parsed, out var error))
            return ("/schedule", "schedule.invalid", error ?? "Invalid schedule.");

        // A five-field expression cannot fire more than once a minute; kept as the one place that states the rule (macros, future formats).
        if (parsed.FiresMoreOftenThan(MinInterval, now))
            return ("/schedule", "schedule.too_frequent", "A task may run at most once a minute.");
        return null;
    }

    internal static void AddScheduleProblem<T>(ValidationContext<T> context, string? schedule, string? timeZone)
    {
        if (CheckSchedule(schedule, timeZone, DateTimeOffset.UtcNow) is not { } problem) return;
        context.AddFailure(new ValidationFailure(problem.Pointer.TrimStart('/'), problem.Message) { ErrorCode = problem.Code });
    }

    public static IRuleBuilderOptions<T, string?> MustBeValidName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(n => !string.IsNullOrWhiteSpace(n)).WithErrorCode("required").WithMessage("A name is required.")
            .MaximumLength(MaxNameLength);

    public static IRuleBuilderOptions<T, string?> MustBeValidCommand<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(c => !string.IsNullOrWhiteSpace(c)).WithErrorCode("required").WithMessage("A command is required.")
            .MaximumLength(MaxCommandLength)
            .Must(c => c is null || !c.Contains('\0')).WithErrorCode("pattern").WithMessage("The command must not contain NUL characters.");

    public static IRuleBuilderOptions<T, string?> MustBeValidComposeService<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(IsValidComposeService).WithErrorCode("pattern")
            .WithMessage("Must start with a letter or digit and contain only letters, digits, '_', '.' and '-' (at most 200 characters).");
}

public sealed class CreateScheduledTaskValidator : AbstractValidator<CreateScheduledTaskRequest>
{
    public CreateScheduledTaskValidator()
    {
        RuleFor(x => x.Name).MustBeValidName();
        RuleFor(x => x.Command).MustBeValidCommand();
        RuleFor(x => x.Schedule).Must(s => !string.IsNullOrWhiteSpace(s)).WithErrorCode("required").WithMessage("A schedule is required.");
        RuleFor(x => x).Custom((x, context) => ScheduledTaskRules.AddScheduleProblem(context, x.Schedule, x.TimeZone))
            .When(x => !string.IsNullOrWhiteSpace(x.Schedule));
        RuleFor(x => x.TimeoutSeconds).InclusiveBetween(ScheduledTaskRules.MinTimeoutSeconds, ScheduledTaskRules.MaxTimeoutSeconds)
            .When(x => x.TimeoutSeconds is not null);
        RuleFor(x => x.ComposeService).MustBeValidComposeService().When(x => x.ComposeService is not null);
    }
}

public sealed class UpdateScheduledTaskValidator : AbstractValidator<UpdateScheduledTaskRequest>
{
    public UpdateScheduledTaskValidator()
    {
        RuleFor(x => x.Name).MustBeValidName().When(x => x.Name is not null);
        RuleFor(x => x.Command).MustBeValidCommand().When(x => x.Command is not null);
        RuleFor(x => x).Custom((x, context) => ScheduledTaskRules.AddScheduleProblem(context, x.Schedule, x.TimeZone))
            .When(x => x.Schedule is not null);
        RuleFor(x => x).Custom((x, context) => ScheduledTaskRules.AddScheduleProblem(context, "* * * * *", x.TimeZone))
            .When(x => x.TimeZone is not null && x.Schedule is null);
        RuleFor(x => x.TimeoutSeconds).InclusiveBetween(ScheduledTaskRules.MinTimeoutSeconds, ScheduledTaskRules.MaxTimeoutSeconds)
            .When(x => x.TimeoutSeconds is not null);
        RuleFor(x => x.ComposeService).MustBeValidComposeService().When(x => x.ComposeService is not null);
    }
}
