using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Agents.Monitoring;

/// <summary>What one retention pass did.</summary>
public sealed record RetentionResult(long RolledUpRaw, long RolledUpFiveMinute, long DeletedHourly, bool Skipped);

/// <summary>
/// Retention and downsampling of <c>metric_samples</c>. Raw samples (every 10 s) are rolled up into 5-minute averages once they are older
/// than <see cref="MetricsRetentionOptions.RawHours"/>, 5-minute rows into hourly ones after <see cref="MetricsRetentionOptions.FiveMinuteDays"/>,
/// hourly rows are deleted after <see cref="MetricsRetentionOptions.HourlyDays"/>. Each roll-up is one statement (
/// <c>WITH moved AS (DELETE ... RETURNING *) INSERT ... SELECT ... GROUP BY bucket</c>) so rows are never lost or counted twice, and the
/// cut-off is aligned to the bucket size so a bucket is never split between two passes. Averages for gauges (CPU, memory, disk, load),
/// the maximum for cumulative counters (network bytes).
/// </summary>
public sealed class MetricsRetentionService(IServiceScopeFactory scopes, IClock clock, IOptions<AgentGatewayOptions> gateway, ILogger<MetricsRetentionService> logger) : BackgroundService
{
    private const long LockKey = 0x4165746865726131; // "Aethera1": one retention pass at a time across API instances

    private readonly MetricsRetentionOptions _options = gateway.Value.Metrics;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !gateway.Value.Enabled || AetheraHost.IsOpenApiGeneration) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds)));
        do
        {
            try
            {
                var result = await RunOnceAsync(clock.UtcNow, stoppingToken);
                if (!result.Skipped && (result.RolledUpRaw > 0 || result.RolledUpFiveMinute > 0 || result.DeletedHourly > 0))
                    logger.LogInformation("Metrics retention: {Raw} raw and {Five} five-minute rows rolled up, {Hourly} hourly rows deleted", result.RolledUpRaw, result.RolledUpFiveMinute, result.DeletedHourly);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Metrics retention pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass as of <paramref name="now"/>. Public so tests (and an admin tool) can run it deterministically.</summary>
    public async Task<RetentionResult> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(cancellationToken);
        if (!locked) return new RetentionResult(0, 0, 0, Skipped: true);

        var rawCutoff = Align(now - _options.Raw, TimeSpan.FromMinutes(5));
        var fiveCutoff = Align(now - _options.FiveMinute, TimeSpan.FromHours(1));
        var hourlyCutoff = now - _options.Hourly;

        var rolledRaw = await RollUpAsync(db, "raw", "fiveMinutes", "5 minutes", rawCutoff, cancellationToken);
        var rolledFive = await RollUpAsync(db, "fiveMinutes", "oneHour", "1 hour", fiveCutoff, cancellationToken);
        var deleted = await db.Database.ExecuteSqlAsync($"DELETE FROM metric_samples WHERE resolution = 'oneHour' AND timestamp < {hourlyCutoff}", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new RetentionResult(rolledRaw, rolledFive, deleted, Skipped: false);
    }

    private const string RollUpSql = """
        WITH moved AS (
            DELETE FROM metric_samples WHERE resolution = '@from' AND timestamp < {0} RETURNING *
        )
        INSERT INTO metric_samples (server_id, container_id, workload_id, timestamp, resolution, cpu_percent, memory_used_bytes, memory_total_bytes,
                                    disk_used_bytes, disk_total_bytes, net_rx_bytes, net_tx_bytes, load1, load5, load15)
        SELECT server_id, container_id, (array_agg(workload_id) FILTER (WHERE workload_id IS NOT NULL))[1],
               date_bin(interval '@bucket', timestamp, timestamptz '2000-01-01 00:00:00+00'), '@to',
               avg(cpu_percent), round(avg(memory_used_bytes))::bigint, max(memory_total_bytes),
               round(avg(disk_used_bytes))::bigint, max(disk_total_bytes), max(net_rx_bytes), max(net_tx_bytes),
               avg(load1), avg(load5), avg(load15)
        FROM moved
        GROUP BY server_id, container_id, date_bin(interval '@bucket', timestamp, timestamptz '2000-01-01 00:00:00+00')
        """;

    // The resolution names and the bucket are constants chosen by RunOnceAsync, never input, so they are substituted into the text.
    private static Task<int> RollUpAsync(AetheraDbContext db, string from, string to, string bucket, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(RollUpSql.Replace("@from", from).Replace("@to", to).Replace("@bucket", bucket), [cutoff], cancellationToken);

    /// <summary>Floors a time to a multiple of <paramref name="bucket"/> since the epoch (the origin of <c>date_bin</c> above has the same alignment).</summary>
    public static DateTimeOffset Align(DateTimeOffset time, TimeSpan bucket)
    {
        var ticks = time.UtcTicks - time.UtcTicks % bucket.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
