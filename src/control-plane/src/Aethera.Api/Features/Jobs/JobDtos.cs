using System.Text.Json;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Jobs;

/// <summary>The resource a job acts on.</summary>
public sealed record JobResourceDto(string Type, Guid Id);

/// <summary>Why a job failed (ProblemDetails-shaped: <c>code</c>, <c>title</c>, <c>detail</c>), plus the failed step for deployments.</summary>
public sealed record JobErrorDto(string Code, string Title, string? Detail, string? FailedStep, bool Retryable);

public sealed record JobLinksDto(string Self, string Logs);

/// <summary>The job resource (ADR 0003 section 5). The same shape is pushed over <c>/hubs/jobs</c>.</summary>
public sealed record JobDto(
    Guid Id,
    string Type,
    JobStatus Status,
    bool CancelRequested,
    int Priority,
    JobResourceDto? Resource,
    int? QueuePosition,
    int Attempt,
    int MaxAttempts,
    int RetryNo,
    DateTimeOffset RunAfter,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    Guid? ParentJobId,
    Guid? CreatedBy,
    JobErrorDto? Error,
    JsonElement? Result,
    JobLinksDto Links);

/// <summary>A page of stored log chunks. Logs paginate by sequence, not by cursor (ADR 0003 section 2).</summary>
/// <param name="Items">Chunks in sequence order; <c>text</c> may hold several lines.</param>
/// <param name="NextSequence">Pass as <c>fromSequence</c> to continue (or to tail a running job).</param>
/// <param name="HasMore">True when the page was cut by <c>limit</c> and more is already stored.</param>
/// <param name="Ended">True when the job is finished and nothing more will be appended after <c>NextSequence</c>.</param>
public sealed record LogPageDto(IReadOnlyList<LogLine> Items, long NextSequence, bool HasMore, bool Ended);

internal static class JobMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JobDto ToDto(Job job, int? queuePosition = null) => new(
        job.Id,
        job.Type,
        job.Status,
        job.CancelRequested,
        job.Priority,
        job.ResourceType is not null && job.ResourceId is { } resourceId ? new JobResourceDto(job.ResourceType, resourceId) : null,
        job.Status == JobStatus.Queued ? queuePosition : null,
        job.Attempt,
        job.MaxAttempts,
        job.RetryNo,
        job.RunAfter,
        job.CreatedAt,
        job.StartedAt,
        job.FinishedAt,
        job.ParentJobId,
        job.CreatedBy,
        ParseError(job.ErrorJson),
        ParseResult(job.ResultJson),
        new JobLinksDto($"/api/v1/jobs/{job.Id:D}", $"/api/v1/jobs/{job.Id:D}/logs"));

    private static JobErrorDto? ParseError(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var error = JsonSerializer.Deserialize<JobError>(json, Json);
            return error is null ? null : new JobErrorDto(error.Code, error.Title, error.Detail, error.FailedStep, error.Retryable);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? ParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 1-based position of each queued job in claim order (priority, run_after, id) among all queued jobs. Computed for the given jobs
    /// in one query.
    /// </summary>
    public static async Task<Dictionary<Guid, int>> QueuePositionsAsync(AetheraDbContext db, IEnumerable<Job> jobs, CancellationToken cancellationToken)
    {
        var ids = jobs.Where(j => j.Status == JobStatus.Queued).Select(j => j.Id).ToArray();
        if (ids.Length == 0) return [];
        var rows = await db.Database.SqlQuery<PositionRow>($"""
            SELECT j.id AS id,
                   ((SELECT count(*) FROM jobs o
                     WHERE o.status = 'queued'
                       AND (o.priority > j.priority OR (o.priority = j.priority AND (o.run_after, o.id) < (j.run_after, j.id)))) + 1)::int AS position
            FROM jobs j
            WHERE j.id = ANY({ids})
            """).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => r.Position);
    }

    public static async Task<JobDto> ToDtoWithPositionAsync(AetheraDbContext db, Job job, CancellationToken cancellationToken)
    {
        var positions = await QueuePositionsAsync(db, [job], cancellationToken);
        return ToDto(job, positions.TryGetValue(job.Id, out var position) ? position : null);
    }

    private sealed class PositionRow
    {
        public Guid Id { get; set; }

        public int Position { get; set; }
    }
}
