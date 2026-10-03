using Aethera.Domain;

namespace Aethera.Infrastructure.Jobs;

/// <summary>Payload of <c>system.echo</c>.</summary>
public sealed class EchoPayload
{
    /// <summary>Lines written to the job log, in order.</summary>
    public string[] Lines { get; set; } = [];

    /// <summary>Pause before each line (cancellable). 0 = none.</summary>
    public int DelayMs { get; set; }

    /// <summary>Zero-based index of the line at which the job fails instead of writing it. Null = never fails.</summary>
    public int? FailAt { get; set; }

    /// <summary>Whether the simulated failure is transient (retried with backoff while attempts remain). Default true.</summary>
    public bool Retryable { get; set; } = true;
}

/// <summary>
/// Built-in <c>system.echo</c> job: writes <see cref="EchoPayload.Lines"/> to the job log with an optional delay and failure point.
/// It exercises the whole pipeline (queue, worker, lease, logs, live fan-out, retry, cancel) for tests, smoke checks and the UI.
/// </summary>
public sealed class EchoJobHandler : IJobHandler
{
    public const string JobType = "system.echo";

    public string Type => JobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.GetPayload<EchoPayload>();
        var total = payload.Lines.Length;
        for (var i = 0; i < total; i++)
        {
            if (payload.DelayMs > 0) await Task.Delay(payload.DelayMs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (payload.FailAt == i)
            {
                var error = new JobError("echo.failed", "Echo failed", $"Simulated failure at line {i}.", FailedStep: null, payload.Retryable);
                throw new JobFailedException(error);
            }

            await context.Log.WriteAsync(LogStream.Stdout, payload.Lines[i], cancellationToken);
            context.ReportProgress((i + 1) * 100 / total, $"line {i + 1} of {total}");
        }

        if (payload.FailAt is { } failAt && failAt >= total)
            throw new JobFailedException(new JobError("echo.failed", "Echo failed", $"Simulated failure after {total} lines.", null, payload.Retryable));

        context.SetResult(new { lines = total });
    }
}
