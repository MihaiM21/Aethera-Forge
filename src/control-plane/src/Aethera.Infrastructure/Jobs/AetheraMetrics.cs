using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Prometheus;

namespace Aethera.Infrastructure.Jobs;

/// <summary>
/// The Prometheus metrics of the control plane, in a registry of their own (not the process-wide default) so that several hosts in one
/// process (tests) stay independent. <c>/metrics</c> exports <see cref="Registry"/>.
/// </summary>
/// <remarks>
/// The queue gauges (<c>aethera_jobs_queued</c>, <c>aethera_jobs_running</c>) are read from Postgres when scraped, so they are cluster
/// wide and right even when another instance did the work; everything else is counted by the process that observed the event.
/// </remarks>
public sealed class AetheraMetrics
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AetheraMetrics> _logger;
    private readonly HashSet<string> _seenQueued = [];
    private readonly HashSet<string> _seenRunning = [];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public AetheraMetrics(IServiceProvider services, ILogger<AetheraMetrics> logger)
    {
        _services = services;
        _logger = logger;
        Registry = Metrics.NewCustomRegistry();
        var factory = Factory = Metrics.WithCustomRegistry(Registry);

        Queued = factory.CreateGauge("aethera_jobs_queued", "Jobs waiting in the queue, by type (all instances).",
            new GaugeConfiguration { LabelNames = ["type"] });
        Running = factory.CreateGauge("aethera_jobs_running", "Jobs currently running, by type (all instances).",
            new GaugeConfiguration { LabelNames = ["type"] });
        Finished = factory.CreateCounter("aethera_jobs_finished_total", "Jobs that reached a final state on this instance.",
            new CounterConfiguration { LabelNames = ["type", "status"] });
        Failures = factory.CreateCounter("aethera_job_failures_total", "Job executions that ended in a reported failure (including ones that were retried).",
            new CounterConfiguration { LabelNames = ["type", "code"] });
        Retries = factory.CreateCounter("aethera_job_retries_total", "Failed executions that were re-queued with backoff.",
            new CounterConfiguration { LabelNames = ["type"] });
        Duration = factory.CreateHistogram("aethera_job_duration_seconds", "Wall-clock time of one execution of a job, by type and outcome.",
            new HistogramConfiguration
            {
                LabelNames = ["type", "status"],
                Buckets = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600, 1800],
            });
        Workers = factory.CreateGauge("aethera_job_workers", "Job slots currently running on this instance.");
        LeaseReaps = factory.CreateCounter("aethera_job_lease_reaps_total", "Jobs taken back from workers whose lease expired.",
            new CounterConfiguration { LabelNames = ["outcome"] });
        Claims = factory.CreateCounter("aethera_job_claims_total", "Jobs claimed by this instance.");
        LogChunksWritten = factory.CreateCounter("aethera_log_chunks_written_total", "Log chunks written to Postgres by this instance.");
        LogPublishFailures = factory.CreateCounter("aethera_log_publish_failures_total", "Log batches that could not be stored after retries (data lost).");

        Registry.AddBeforeCollectCallback(async cancellationToken => await RefreshQueueGaugesAsync(cancellationToken));
    }

    public CollectorRegistry Registry { get; }

    /// <summary>Creates metrics in <see cref="Registry"/> (used by the HTTP request middleware).</summary>
    public IMetricFactory Factory { get; }

    public Gauge Queued { get; }
    public Gauge Running { get; }
    public Counter Finished { get; }
    public Counter Failures { get; }
    public Counter Retries { get; }
    public Histogram Duration { get; }
    public Gauge Workers { get; }
    public Counter LeaseReaps { get; }
    public Counter Claims { get; }
    public Counter LogChunksWritten { get; }
    public Counter LogPublishFailures { get; }

    private async Task RefreshQueueGaugesAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var dataSource = _services.GetService<NpgsqlDataSource>();
            if (dataSource is null) return;

            await using var command = dataSource.CreateCommand("SELECT type, status, count(*) FROM jobs WHERE status IN ('queued','running') GROUP BY type, status");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var queued = new Dictionary<string, long>();
            var running = new Dictionary<string, long>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var target = reader.GetString(1) == "queued" ? queued : running;
                target[reader.GetString(0)] = reader.GetInt64(2);
            }

            Apply(Queued, queued, _seenQueued);
            Apply(Running, running, _seenRunning);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No database (tests, start-up): the gauges keep their last values.
            _logger.LogDebug(ex, "Could not refresh the queue gauges");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static void Apply(Gauge gauge, Dictionary<string, long> counts, HashSet<string> seen)
    {
        foreach (var (type, count) in counts)
        {
            gauge.WithLabels(type).Set(count);
            seen.Add(type);
        }

        foreach (var type in seen.Where(t => !counts.ContainsKey(t)))
            gauge.WithLabels(type).Set(0);
    }
}
