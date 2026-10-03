using System.Text.Json;
using Aethera.Domain;

namespace Aethera.Infrastructure.Jobs;

/// <summary>
/// Executes one job type (ADR 0004). Register implementations with DI
/// (<c>services.AddSingleton&lt;IJobHandler, MyHandler&gt;()</c>); the worker host finds them by <see cref="Type"/>.
/// A job whose type has no handler fails with <c>job.unknown_type</c>.
/// </summary>
/// <remarks>
/// Handlers must be <b>resumable</b>: a job is re-run after a worker crash or a graceful shutdown, so persist checkpoints (for
/// deployments: <c>deployment_steps</c>) and use idempotency keys (<see cref="JobContext.IdempotencyKey"/>) for external commands.
/// Throw <see cref="JobFailedException"/> to fail with a structured error; any other exception fails the job as non-retryable
/// <c>job.handler_failed</c> without leaking the exception message. Honour the cancellation token: it fires on a cancel request,
/// lease loss and host shutdown. One handler instance serves all jobs of its type concurrently (singleton): keep it stateless.
/// </remarks>
public interface IJobHandler
{
    /// <summary>The job type this handler runs, <c>area.verb</c> (<c>application.deploy</c>, <c>system.echo</c>).</summary>
    string Type { get; }

    /// <summary>
    /// True (default) when a job of this type may be put back to Queued after its worker died (see <c>Job.ExpireLease</c>);
    /// false makes the reaper fail it with <c>job.worker_lost</c>.
    /// </summary>
    bool Resumable => true;

    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>Everything a handler gets for one execution of a job.</summary>
public sealed class JobContext
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly Action<int, string?> _progress;

    public JobContext(
        Job job,
        IServiceProvider services,
        ILogSink log,
        ISecretRedactor secrets,
        CancellationToken cancellationToken,
        Action<int, string?>? reportProgress = null)
    {
        Job = job;
        Services = services;
        Log = log;
        Secrets = secrets;
        CancellationToken = cancellationToken;
        _progress = reportProgress ?? ((_, _) => { });
    }

    /// <summary>A snapshot of the job row as claimed (detached; changes to it are not persisted).</summary>
    public Job Job { get; }

    public Guid JobId => Job.Id;

    /// <summary>Executions started including this one (1-based).</summary>
    public int Attempt => Job.Attempt;

    /// <summary>Bumps after each reported failure; stable across crash redelivery.</summary>
    public int RetryNo => Job.RetryNo;

    /// <summary>A DI scope that lives as long as the execution.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The <c>job:&lt;id&gt;</c> log stream. Values registered with <see cref="Secrets"/> are masked before anything is stored or published.</summary>
    public ILogSink Log { get; }

    /// <summary>Register every secret value the handler resolves, so it is replaced by <c>********</c> in logs and error details.</summary>
    public ISecretRedactor Secrets { get; }

    /// <summary>Fires on cancel request, lease loss and host shutdown (the same token is passed to <see cref="IJobHandler.ExecuteAsync"/>).</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The job payload as a JSON document.</summary>
    public JsonElement Payload => JsonSerializer.Deserialize<JsonElement>(Job.PayloadJson);

    /// <summary>Deserializes the payload (camelCase, case-insensitive). An empty payload gives a default-constructed value.</summary>
    public T GetPayload<T>() where T : new() =>
        JsonSerializer.Deserialize<T>(Job.PayloadJson, PayloadJson) ?? new T();

    /// <summary>Like <see cref="GetPayload{T}()"/> for record types without a parameterless constructor.</summary>
    public T? GetPayloadOrDefault<T>() => JsonSerializer.Deserialize<T>(Job.PayloadJson, PayloadJson);

    /// <summary>The agent idempotency key of a step: <c>&lt;job_id&gt;:&lt;step&gt;:&lt;retry_no&gt;</c> (ADR 0004).</summary>
    public string IdempotencyKey(string step) => $"{Job.Id}:{step}:{Job.RetryNo}";

    /// <summary>Stored as the job's <c>result</c> when the handler returns normally.</summary>
    public object? Result { get; private set; }

    public void SetResult(object? result) => Result = result;

    /// <summary>
    /// Publishes a transient progress update (0-100) to <c>/hubs/jobs</c> subscribers. Not persisted: after a reconnect the UI
    /// shows the status only.
    /// </summary>
    public void ReportProgress(int percent, string? message = null) => _progress(Math.Clamp(percent, 0, 100), message);
}

/// <summary>Throw from a handler to fail the job with a structured error (ADR 0004 section 3: retryable vs deterministic).</summary>
public sealed class JobFailedException(JobError error, Exception? inner = null) : Exception(error.Detail ?? error.Title, inner)
{
    public JobError Error { get; } = error;

    /// <summary>Transient failure (agent unavailable, registry 5xx...): retried with backoff while attempts remain.</summary>
    public static JobFailedException Transient(string code, string title, string? detail = null, string? failedStep = null, Exception? inner = null) =>
        new(new JobError(code, title, detail, failedStep, Retryable: true), inner);

    /// <summary>Deterministic failure (validation, build failed...): retrying cannot help.</summary>
    public static JobFailedException Permanent(string code, string title, string? detail = null, string? failedStep = null, Exception? inner = null) =>
        new(new JobError(code, title, detail, failedStep, Retryable: false), inner);
}

/// <summary>Where a handler writes the job's log. Lines are chunked, redacted, persisted in batches and published live.</summary>
public interface ILogSink : IAsyncDisposable
{
    string StreamId { get; }

    /// <summary>Appends a line (a newline is added when missing). Never blocks on the database.</summary>
    ValueTask WriteAsync(LogStream stream, string line, CancellationToken cancellationToken = default);

    /// <summary>Appends control-plane narration (stdout, prefixed <c>[aethera]</c>), as opposed to output of the work itself.</summary>
    ValueTask WriteSystemAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>Seals the pending chunk and returns once everything written so far is stored in Postgres (and published).</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates sinks for log streams (<c>job:&lt;id&gt;</c>, later <c>build:&lt;id&gt;</c>...). Continues the stream's sequence numbers.</summary>
public interface ILogSinkFactory
{
    Task<ILogSink> CreateAsync(string streamId, ISecretRedactor? redactor = null, LogSource source = LogSource.Job, CancellationToken cancellationToken = default);
}

/// <summary>Masks registered secret values (<c>********</c>) in text before it is persisted or published.</summary>
public interface ISecretRedactor
{
    public const string Mask = "********";

    /// <summary>Starts masking <paramref name="value"/> (and, for multi-line values, each of its lines). Null, empty and values shorter than 3 characters are ignored.</summary>
    void Register(string? value);

    string Redact(string text);
}

/// <summary>Thread-safe <see cref="ISecretRedactor"/>. Longer secrets are replaced first, so one containing another is masked fully.</summary>
public sealed class SecretRedactor : ISecretRedactor
{
    private const int MinLength = 3;
    private readonly Lock _gate = new();
    private string[] _values = [];

    public void Register(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var candidates = new List<string>();
        if (value.Length >= MinLength) candidates.Add(value);
        if (value.Contains('\n') || value.Contains('\r'))
        {
            foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (line.Length >= MinLength) candidates.Add(line);
        }

        if (candidates.Count == 0) return;
        lock (_gate)
        {
            _values = _values.Concat(candidates).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToArray();
        }
    }

    public string Redact(string text)
    {
        var values = Volatile.Read(ref _values);
        if (values.Length == 0 || string.IsNullOrEmpty(text)) return text;
        foreach (var value in values)
            if (text.Contains(value, StringComparison.Ordinal))
                text = text.Replace(value, ISecretRedactor.Mask, StringComparison.Ordinal);
        return text;
    }
}
