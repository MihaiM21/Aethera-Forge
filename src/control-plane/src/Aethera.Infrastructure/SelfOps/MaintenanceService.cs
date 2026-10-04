using System.Text.Json;
using Aethera.Domain;
using Aethera.Infrastructure.Agents.Jobs;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.SelfOps;

/// <summary>What one maintenance pass did. Stored as the "last run" shown on the system page.</summary>
public sealed record MaintenanceReport(
    DateTimeOffset At,
    long LogChunksDeleted,
    long EventsDeleted,
    long WebhookDeliveriesDeleted,
    long AuditEventsDeleted,
    long SessionsDeleted,
    long IdempotencyRecordsDeleted,
    int PruneJobsQueued,
    string? BackupTaken,
    int BackupsDeleted,
    IReadOnlyList<string> Errors,
    bool Skipped = false);

/// <summary>When each scheduled task last ran; survives restarts (an <c>instance_settings</c> row) so a restart does not repeat a weekly prune.</summary>
public sealed record MaintenanceState(DateTimeOffset? LastBackupAt, DateTimeOffset? LastPruneAt, MaintenanceReport? LastRun);

/// <summary>
/// WP5.2 self-ops, one pass per hour: log and event retention, the database backup (daily, newest seven kept) and the server prune
/// (weekly, through the normal <c>server.prune</c> job, so it is visible in Jobs and respects rollback images). Every task catches its
/// own failure, reports it and lets the others run. One pass at a time across API instances (advisory lock). <see cref="RunOnceAsync"/>
/// is public for tests and the "Run now" button.
/// </summary>
public class MaintenanceService(
    IServiceScopeFactory scopes, IClock clock, IOptions<MaintenanceOptions> options, IConfiguration configuration, AgentSessionRegistry sessions,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    public const string StateKey = "maintenance.state";
    private const long LockKey = 0x4165746865726132; // "Aethera2"
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || AetheraHost.IsOpenApiGeneration) return;
        try
        {
            // Start-up is busy (migrations, agents reconnecting): the first pass can wait.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(options.Value.Interval);
            do
            {
                try
                {
                    var report = await RunOnceAsync(clock.UtcNow, force: false, stoppingToken);
                    if (report.Errors.Count > 0) logger.LogWarning("Maintenance finished with {Count} error(s): {Errors}", report.Errors.Count, string.Join("; ", report.Errors));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Maintenance pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public virtual IDatabaseBackup? CreateBackup()
    {
        var connectionString = configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName);
        return string.IsNullOrWhiteSpace(connectionString) ? null : new DatabaseBackup(connectionString, options.Value.BackupDirectory, logger);
    }

    /// <param name="force">Run the backup and the prune now, whatever their schedule says (the "Run now" button).</param>
    public async Task<MaintenanceReport> RunOnceAsync(DateTimeOffset now, bool force, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(cancellationToken);
        if (!locked) return new MaintenanceReport(now, 0, 0, 0, 0, 0, 0, 0, null, 0, [], Skipped: true);

        var state = await LoadStateAsync(db, cancellationToken);
        var errors = new List<string>();
        long logs = 0, events = 0, deliveries = 0, audit = 0, sessionRows = 0, idempotency = 0;
        int pruneJobs = 0, backupsDeleted = 0;
        string? backupTaken = null;
        var lastBackup = state.LastBackupAt;
        var lastPrune = state.LastPruneAt;

        // ---- retention ------------------------------------------------------------------------------------------------------
        try
        {
            if (settings.LogRetentionDays > 0)
            {
                var cutoff = now.AddDays(-settings.LogRetentionDays);
                logs = await db.LogChunks.Where(c => c.Timestamp < cutoff).ExecuteDeleteAsync(cancellationToken);
            }
            if (settings.EventRetentionDays > 0)
            {
                var cutoff = now.AddDays(-settings.EventRetentionDays);
                events = await db.ResourceEvents.Where(e => e.OccurredAt < cutoff).ExecuteDeleteAsync(cancellationToken);
                deliveries = await db.WebhookDeliveries.Where(d => d.ReceivedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            }
            if (settings.AuditRetentionDays > 0)
            {
                var cutoff = now.AddDays(-settings.AuditRetentionDays);
                // Audit rows are append-only through EF (the context refuses deletes); retention is the one sanctioned delete, in SQL.
                audit = await db.Database.ExecuteSqlAsync($"DELETE FROM audit_events WHERE occurred_at < {cutoff}", cancellationToken);
            }
            if (settings.SessionRetentionDays > 0)
            {
                var cutoff = now.AddDays(-settings.SessionRetentionDays);
                sessionRows = await db.UserSessions.Where(s => s.ExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff)).ExecuteDeleteAsync(cancellationToken);
                idempotency = await db.IdempotencyRecords.Where(r => r.ExpiresAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"retention: {ex.Message}");
        }

        // ---- database backup ------------------------------------------------------------------------------------------------
        if (settings.BackupEnabled && (force || Due(lastBackup, now, TimeSpan.FromHours(Math.Max(0.01, settings.BackupIntervalHours)))))
        {
            try
            {
                var backup = CreateBackup();
                if (backup is null) errors.Add("backup: no database connection string");
                else if (!backup.IsAvailable()) errors.Add("backup: pg_dump is not installed next to the control plane");
                else
                {
                    var file = await backup.BackupAsync(force ? "manual" : "scheduled", now, cancellationToken);
                    backupTaken = file.Name;
                    lastBackup = now;
                    // Scheduled and manual backups share one retention count; a manual one is a scheduled one a person asked for early.
                    backupsDeleted += backup.Prune("scheduled", settings.BackupsKept) + backup.Prune("manual", settings.BackupsKept);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"backup: {ex.Message}");
            }
        }

        // ---- server prune ---------------------------------------------------------------------------------------------------
        if (settings.PruneEnabled && (force || Due(lastPrune, now, TimeSpan.FromHours(Math.Max(0.01, settings.PruneIntervalHours)))))
        {
            try
            {
                pruneJobs = await QueuePrunesAsync(scope.ServiceProvider, db, now, cancellationToken);
                lastPrune = now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"prune: {ex.Message}");
            }
        }

        var report = new MaintenanceReport(now, logs, events, deliveries, audit, sessionRows, idempotency, pruneJobs, backupTaken, backupsDeleted, errors);
        await SaveStateAsync(db, new MaintenanceState(lastBackup, lastPrune, report), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return report;
    }

    /// <summary>
    /// One <c>server.prune</c> job per connected server: dangling and unused images older than <see cref="MaintenanceOptions.PruneKeepHours"/>,
    /// unused networks and build cache. Never volumes (data) and never stopped containers (a stopped application must be startable again);
    /// the handler adds every rollback image of the server to the keep list.
    /// </summary>
    private async Task<int> QueuePrunesAsync(IServiceProvider services, AetheraDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queue = services.GetRequiredService<IJobQueue>();
        var connected = sessions.ConnectedServerIds;
        if (connected.Count == 0) return 0;
        var servers = await db.Servers.AsNoTracking().Where(s => connected.Contains(s.Id)).Select(s => new { s.Id, s.OrganizationId }).ToListAsync(cancellationToken);
        foreach (var server in servers)
        {
            await queue.EnqueueAsync(new JobRequest(ServerPruneJobHandler.JobType, new ServerPrunePayload
            {
                ServerId = server.Id,
                DanglingImages = true,
                UnusedImages = true,
                UnusedNetworks = true,
                BuildCache = true,
                OlderThanHours = options.Value.PruneKeepHours,
            })
            {
                OrganizationId = server.OrganizationId,
                Resource = new JobResource("server", server.Id),
                LockKey = $"server:{server.Id}:maintenance",
                IdempotencyKey = $"maintenance-prune:{server.Id}:{now.UtcDateTime:yyyyMMdd}",
            }, cancellationToken);
        }
        return servers.Count;
    }

    private static bool Due(DateTimeOffset? last, DateTimeOffset now, TimeSpan interval) => last is null || now - last.Value >= interval;

    public async Task<MaintenanceState> GetStateAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await LoadStateAsync(scope.ServiceProvider.GetRequiredService<AetheraDbContext>(), cancellationToken);
    }

    private static async Task<MaintenanceState> LoadStateAsync(AetheraDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == StateKey, cancellationToken);
        if (row is null) return new MaintenanceState(null, null, null);
        try
        {
            return JsonSerializer.Deserialize<MaintenanceState>(row.ValueJson, Json) ?? new MaintenanceState(null, null, null);
        }
        catch (JsonException)
        {
            return new MaintenanceState(null, null, null); // a damaged row only means "never ran": the tasks run again
        }
    }

    private static async Task SaveStateAsync(AetheraDbContext db, MaintenanceState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(state, Json);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == StateKey, cancellationToken);
        if (row is null) db.Settings.Add(new InstanceSetting { Key = StateKey, ValueJson = json, UpdatedAt = now });
        else { row.ValueJson = json; row.UpdatedAt = now; }
        await db.SaveChangesAsync(cancellationToken);
    }
}
