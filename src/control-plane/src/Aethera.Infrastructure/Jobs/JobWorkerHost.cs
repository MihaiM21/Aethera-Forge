using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aethera.Infrastructure.Jobs;

/// <summary>
/// The worker side of the job system (ADR 0004): <c>WorkerCount</c> slots that claim and run jobs, a <c>LISTEN</c> connection that wakes
/// idle slots and delivers cancel requests, and the lease reaper. Any number of hosts (processes, instances) may run against one
/// database: claiming uses <c>FOR UPDATE SKIP LOCKED</c>, jobs sharing a lock key additionally hold a session-level advisory lock, so
/// a job never runs twice at the same time.
/// </summary>
public sealed class JobWorkerHost : BackgroundService
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    private readonly JobStore _store;
    private readonly IServiceProvider _services;
    private NpgsqlDataSource _dataSource => _services.GetRequiredService<NpgsqlDataSource>();
    private readonly IServiceScopeFactory _scopes;
    private readonly Dictionary<string, IJobHandler> _handlers;
    private readonly ILogSinkFactory _sinks;
    private readonly LogIngestor _ingestor;
    private readonly JobEvents _events;
    private readonly AetheraMetrics _metrics;
    private readonly WorkerWakeSignal _wake;
    private readonly IClock _clock;
    private readonly JobsOptions _options;
    private readonly ILogger<JobWorkerHost> _logger;
    private readonly ConcurrentDictionary<Guid, JobRun> _running = new();

    public JobWorkerHost(
        JobStore store,
        IServiceProvider services,
        IServiceScopeFactory scopes,
        IEnumerable<IJobHandler> handlers,
        ILogSinkFactory sinks,
        LogIngestor ingestor,
        JobEvents events,
        AetheraMetrics metrics,
        WorkerWakeSignal wake,
        IClock clock,
        IOptions<JobsOptions> options,
        ILogger<JobWorkerHost> logger)
    {
        _store = store;
        _services = services;
        _scopes = scopes;
        _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);
        _sinks = sinks;
        _ingestor = ingestor;
        _events = events;
        _metrics = metrics;
        _wake = wake;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        var generated = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        WorkerId = string.IsNullOrWhiteSpace(_options.WorkerId) ? generated[..Math.Min(200, generated.Length)] : _options.WorkerId;
    }

    /// <summary>Stored in <c>jobs.locked_by</c> while this host runs a job.</summary>
    public string WorkerId { get; }

    /// <summary>Jobs this host is executing right now.</summary>
    public int RunningCount => _running.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.WorkerCount <= 0)
        {
            _logger.LogInformation("Job workers are disabled (Aethera:Jobs:WorkerCount = 0)");
            return;
        }

        try { _ = _dataSource; }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job workers are idle: {Reason}", ex.Message);
            return;
        }

        _logger.LogInformation("Job worker host {WorkerId} starting with {Slots} slots, lease {Lease}s, poll {Poll}s",
            WorkerId, _options.WorkerCount, _options.LeaseSeconds, _options.PollSeconds);

        var tasks = new List<Task> { Task.Run(() => ListenLoopAsync(stoppingToken), CancellationToken.None), Task.Run(() => ReaperLoopAsync(stoppingToken), CancellationToken.None) };
        for (var i = 0; i < _options.WorkerCount; i++)
        {
            var slot = i;
            tasks.Add(Task.Run(() => SlotLoopAsync(slot, stoppingToken), CancellationToken.None));
        }

        await Task.WhenAll(tasks);
        _logger.LogInformation("Job worker host {WorkerId} stopped", WorkerId);
    }

    // ----------------------------------------------------------------- slots

    private async Task SlotLoopAsync(int slot, CancellationToken stopping)
    {
        _metrics.Workers.Inc();
        var failures = 0;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    var signal = _wake.Current; // before looking for work: a pulse while we look is not lost
                    var job = await _store.ClaimAsync(WorkerId, _options.Lease, stopping);
                    failures = 0;
                    if (job is null)
                    {
                        await WaitForWorkAsync(signal, stopping);
                        continue;
                    }

                    _metrics.Claims.Inc();
                    await RunAsync(job, stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failures++;
                    if (failures == 1 || failures % 12 == 0)
                        _logger.LogWarning(ex, "Job slot {Slot} could not fetch or run work ({Failures} consecutive failures)", slot, failures);
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, _options.Poll.TotalSeconds)), stopping); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            _metrics.Workers.Dec();
        }
    }

    private async Task WaitForWorkAsync(Task signal, CancellationToken stopping)
    {
        var delay = _options.Poll;
        if (await _store.UntilNextRunnableAsync(stopping) is { } until && until < delay)
            delay = until < TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20) : until;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        await Task.WhenAny(signal, Task.Delay(delay, cts.Token));
        await cts.CancelAsync();
    }

    // ------------------------------------------------------------ one job run

    private async Task RunAsync(Job claimed, CancellationToken hostStopping)
    {
        var run = new JobRun(claimed);
        _running[claimed.Id] = run;
        try
        {
            await using var stopRegistration = hostStopping.Register(static state => ((JobRun)state!).Cancel(CancelReason.Shutdown), run);
            await RunCoreAsync(run);
        }
        finally
        {
            _running.TryRemove(claimed.Id, out _);
            run.Dispose();
        }
    }

    private async Task RunCoreAsync(JobRun run)
    {
        var job = run.Job;
        _handlers.TryGetValue(job.Type, out var handler);

        NpgsqlConnection? lockConnection = null;
        JobOutcome outcome;
        ILogSink? sink = null;
        var redactor = new SecretRedactor();
        try
        {
            if (handler is not null && job.LockKey is not null)
            {
                try
                {
                    lockConnection = await TryAcquireLockAsync(job.LockKey);
                }
                catch
                {
                    await _store.ReleaseAsync(job.Id, WorkerId, TimeSpan.FromSeconds(1), unlessCancelRequested: false);
                    throw;
                }

                if (lockConnection is null)
                {
                    // Lost the race for the advisory lock to another instance: back to the queue, attempt not consumed (ADR 0004 section 2).
                    await _store.ReleaseAsync(job.Id, WorkerId, TimeSpan.FromSeconds(_options.LockContentionDelaySeconds), unlessCancelRequested: false);
                    _logger.LogDebug("Job {JobId} lost the advisory lock {LockKey}; requeued", job.Id, job.LockKey);
                    return;
                }
            }

            sink = await _sinks.CreateAsync("job:" + job.Id, redactor, LogSource.Job, CancellationToken.None);
            await _events.PublishUpdatedAsync(job);

            if (handler is null)
            {
                outcome = new JobOutcome.Failed(new JobError("job.unknown_type", "Unknown job type",
                    $"No handler is registered for job type '{job.Type}'.", Retryable: false));
            }
            else
            {
                run.StartHeartbeat(ct => HeartbeatLoopAsync(run, lockConnection, ct));
                await sink.WriteSystemAsync($"Started {job.Type} (attempt {job.Attempt} of {job.MaxAttempts}) on {WorkerId}");
                outcome = await InvokeHandlerAsync(handler, run, sink, redactor);
            }
        }
        finally
        {
            await run.StopHeartbeatAsync();
            await ReleaseLockAsync(lockConnection, job.LockKey);
        }

        await CompleteAsync(run, outcome, sink!);
    }

    private async Task<JobOutcome> InvokeHandlerAsync(IJobHandler handler, JobRun run, ILogSink sink, ISecretRedactor redactor)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var context = new JobContext(run.Job, scope.ServiceProvider, sink, redactor, run.Token,
            (percent, message) => _ = _events.PublishProgressAsync(run.Job, percent, message));
        try
        {
            await handler.ExecuteAsync(context, run.Token);
            return new JobOutcome.Succeeded(context.Result);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            return new JobOutcome.Stopped(run.Reason);
        }
        catch (JobFailedException ex)
        {
            var error = ex.Error with { Detail = ex.Error.Detail is null ? null : redactor.Redact(ex.Error.Detail) };
            return new JobOutcome.Failed(error);
        }
        catch (Exception ex)
        {
            // The message of an arbitrary exception can contain connection strings or secret values: keep it out of the job row.
            _logger.LogError(ex, "Job {JobId} ({Type}) failed with an unhandled exception", run.Job.Id, run.Job.Type);
            return new JobOutcome.Failed(new JobError("job.handler_failed", "Job handler failed",
                $"Unhandled {ex.GetType().Name}; see the server log for details.", Retryable: false));
        }
    }

    private async Task CompleteAsync(JobRun run, JobOutcome outcome, ILogSink sink)
    {
        var job = run.Job;
        var elapsed = Stopwatch.GetElapsedTime(run.StartedAt);
        string status;
        Job? saved = null;

        switch (outcome)
        {
            case JobOutcome.Succeeded ok:
                await sink.WriteSystemAsync($"Succeeded in {elapsed.TotalSeconds:0.##}s");
                await sink.FlushAsync();
                var resultJson = ok.Result is null ? null : JsonSerializer.Serialize(ok.Result, ResultJson);
                saved = await _store.TransitionAsync(job.Id, WorkerId, j => j.Succeed(_clock.UtcNow, resultJson));
                status = "succeeded";
                break;

            case JobOutcome.Failed failed:
                var willRetry = failed.Error.Retryable && job.Attempt < job.MaxAttempts;
                await sink.WriteSystemAsync(willRetry
                    ? $"Attempt {job.Attempt} of {job.MaxAttempts} failed ({failed.Error.Code}): {failed.Error.Detail ?? failed.Error.Title}. Retrying with backoff."
                    : $"Failed ({failed.Error.Code}): {failed.Error.Detail ?? failed.Error.Title}");
                await sink.FlushAsync();
                var requeued = false;
                saved = await _store.TransitionAsync(job.Id, WorkerId,
                    j => requeued = j.Fail(failed.Error, _clock.UtcNow, _options.Backoff(j.Attempt)));
                status = saved is null ? "lost" : requeued ? "retried" : saved.Status == JobStatus.Cancelled ? "cancelled" : "failed";
                _metrics.Failures.WithLabels(job.Type, failed.Error.Code).Inc();
                if (requeued) _metrics.Retries.WithLabels(job.Type).Inc();
                break;

            case JobOutcome.Stopped { Reason: CancelReason.Cancel }:
                await sink.WriteSystemAsync("Cancelled");
                await sink.FlushAsync();
                saved = await _store.TransitionAsync(job.Id, WorkerId, j => j.MarkCancelled(_clock.UtcNow));
                status = "cancelled";
                break;

            case JobOutcome.Stopped { Reason: CancelReason.Shutdown }:
                await sink.WriteSystemAsync("Worker is shutting down; the job goes back to the queue");
                await sink.FlushAsync();
                if (await _store.ReleaseAsync(job.Id, WorkerId, TimeSpan.Zero, unlessCancelRequested: true))
                {
                    status = "released";
                    saved = await ReloadAsync(job.Id);
                }
                else
                {
                    // A cancel request is pending (or the lease is gone): finish it as cancelled when it is still ours.
                    saved = await _store.TransitionAsync(job.Id, WorkerId, j =>
                    {
                        if (j.CancelRequested) j.MarkCancelled(_clock.UtcNow);
                    });
                    status = saved is { Status: JobStatus.Cancelled } ? "cancelled" : "lost";
                }

                break;

            default: // Stopped / lease lost
                _logger.LogWarning("Job {JobId} lost its lease while running on {WorkerId}; its result is discarded", job.Id, WorkerId);
                status = "lost";
                break;
        }

        _metrics.Duration.WithLabels(job.Type, status).Observe(elapsed.TotalSeconds);
        if (saved is not null)
        {
            if (saved.IsTerminal)
            {
                _metrics.Finished.WithLabels(job.Type, ToLabel(saved.Status)).Inc();
                _ingestor.CompleteStream(sink.StreamId, ToLabel(saved.Status));
            }

            await _events.PublishUpdatedAsync(saved);
        }

        await sink.DisposeAsync();

        // A finished job frees its lock key and a retry has a run_after: let idle slots (here and on other instances) look again.
        _wake.Pulse();
        await _store.NotifyAsync(JobStore.WakeChannel, job.Id.ToString());
    }

    private async Task<Job?> ReloadAsync(Guid id)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        return await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
    }

    private static string ToLabel(JobStatus status) => status switch
    {
        JobStatus.Succeeded => "succeeded",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled => "cancelled",
        JobStatus.Running => "running",
        _ => "queued",
    };

    // ----------------------------------------------------- heartbeat and locks

    private async Task HeartbeatLoopAsync(JobRun run, NpgsqlConnection? lockConnection, CancellationToken ct)
    {
        var lastSuccess = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.Heartbeat, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                if (lockConnection is not null)
                {
                    // The advisory lock lives and dies with this connection: if it is gone, mutual exclusion is gone.
                    await using var ping = new NpgsqlCommand("SELECT 1", lockConnection);
                    await ping.ExecuteScalarAsync(ct);
                }

                var heartbeat = await _store.HeartbeatAsync(run.Job.Id, WorkerId, _options.Lease, ct);
                if (!heartbeat.LeaseHeld)
                {
                    run.Cancel(CancelReason.LeaseLost);
                    return;
                }

                lastSuccess = Stopwatch.GetTimestamp();
                if (heartbeat.CancelRequested) run.Cancel(CancelReason.Cancel);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat of job {JobId} failed", run.Job.Id);
                if (lockConnection is not null && lockConnection.State != System.Data.ConnectionState.Open
                    || Stopwatch.GetElapsedTime(lastSuccess) > _options.Lease)
                {
                    run.Cancel(CancelReason.LeaseLost);
                    return;
                }
            }
        }
    }

    private async Task<NpgsqlConnection?> TryAcquireLockAsync(string lockKey)
    {
        // A dedicated connection (not from the EF context): it holds the session-level lock for the whole job and releases it by dying.
        var connection = await _dataSource.OpenConnectionAsync();
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended($1, 0))", connection);
            command.Parameters.AddWithValue(lockKey);
            if ((bool)(await command.ExecuteScalarAsync())!) return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    private async Task ReleaseLockAsync(NpgsqlConnection? connection, string? lockKey)
    {
        if (connection is null) return;
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended($1, 0))", connection);
            command.Parameters.AddWithValue(lockKey!);
            await command.ExecuteScalarAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Releasing advisory lock {LockKey} failed; closing the connection releases it", lockKey);
        }
        finally
        {
            await connection.DisposeAsync(); // returning it to the pool resets the session, which also drops any lock left
        }
    }

    // ------------------------------------------------------- LISTEN / NOTIFY

    private async Task ListenLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(stopping);
                connection.Notification += OnNotification;
                await using (var listen = new NpgsqlCommand($"LISTEN {JobStore.WakeChannel}; LISTEN {JobStore.CancelChannel}", connection))
                    await listen.ExecuteNonQueryAsync(stopping);

                _wake.Pulse(); // anything enqueued while we were not listening
                while (!stopping.IsCancellationRequested)
                    await connection.WaitAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The LISTEN connection failed; polling continues");
                try { await Task.Delay(TimeSpan.FromSeconds(1), stopping); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
    {
        if (e.Channel == JobStore.CancelChannel)
        {
            if (Guid.TryParse(e.Payload, out var id) && _running.TryGetValue(id, out var run)) run.Cancel(CancelReason.Cancel);
        }
        else
        {
            _wake.Pulse();
        }
    }

    // ------------------------------------------------------------------ reaper

    private async Task ReaperLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.Reaper, stopping);
                var reaped = await _store.ReapExpiredAsync(type => _handlers.TryGetValue(type, out var h) && h.Resumable, 50, stopping);
                foreach (var (job, requeued) in reaped)
                    await AfterReapAsync(job, requeued);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The lease reaper failed; it will try again");
            }
        }
    }

    private async Task AfterReapAsync(Job job, bool requeued)
    {
        var outcome = requeued ? "requeued" : job.Status == JobStatus.Cancelled ? "cancelled" : "failed";
        _metrics.LeaseReaps.WithLabels(outcome).Inc();
        _logger.LogWarning("Lease of job {JobId} ({Type}) expired: {Outcome}", job.Id, job.Type, outcome);

        try
        {
            await using var sink = await _sinks.CreateAsync("job:" + job.Id, null, LogSource.Job, CancellationToken.None);
            await sink.WriteSystemAsync(requeued
                ? "The worker stopped renewing its lease; the job was put back in the queue"
                : outcome == "cancelled" ? "The worker stopped renewing its lease and a cancel was pending; the job was cancelled"
                : "The worker stopped renewing its lease; the job failed (job.worker_lost)");
            await sink.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write the reap note of job {JobId}", job.Id);
        }

        if (job.IsTerminal)
        {
            _metrics.Finished.WithLabels(job.Type, ToLabel(job.Status)).Inc();
            _ingestor.CompleteStream("job:" + job.Id, ToLabel(job.Status));
        }

        await _events.PublishUpdatedAsync(job);
        _wake.Pulse();
        await _store.NotifyAsync(JobStore.WakeChannel, job.Id.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private enum CancelReason
    {
        None = 0,
        Cancel = 1,
        LeaseLost = 2,
        Shutdown = 3,
    }

    private abstract record JobOutcome
    {
        public sealed record Succeeded(object? Result) : JobOutcome;

        public sealed record Failed(JobError Error) : JobOutcome;

        public sealed record Stopped(CancelReason Reason) : JobOutcome;
    }

    /// <summary>State of one execution on this host: its cancellation token (and why it fired) and its heartbeat.</summary>
    private sealed class JobRun(Job job) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationTokenSource _heartbeatCts = new();
        private int _reason;
        private Task _heartbeat = Task.CompletedTask;

        public Job Job { get; } = job;

        public long StartedAt { get; } = Stopwatch.GetTimestamp();

        public CancellationToken Token => _cts.Token;

        public CancelReason Reason => (CancelReason)Volatile.Read(ref _reason);

        /// <summary>Fires the token. The first reason wins.</summary>
        public void Cancel(CancelReason reason)
        {
            Interlocked.CompareExchange(ref _reason, (int)reason, 0);
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void StartHeartbeat(Func<CancellationToken, Task> loop) => _heartbeat = Task.Run(() => loop(_heartbeatCts.Token), CancellationToken.None);

        public async Task StopHeartbeatAsync()
        {
            await _heartbeatCts.CancelAsync();
            try { await _heartbeat; }
            catch (Exception) { /* the loop logs its own failures */ }
        }

        public void Dispose()
        {
            _cts.Dispose();
            _heartbeatCts.Dispose();
        }
    }
}
