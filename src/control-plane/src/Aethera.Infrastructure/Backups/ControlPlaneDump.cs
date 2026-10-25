using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.SelfOps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Backups;

/// <summary>A <c>pg_dump -Fc</c> of Aethera's own database in a private temporary folder, deleted when disposed.</summary>
public sealed class ControlPlaneDumpFile(string directory, string path) : IAsyncDisposable
{
    public string Path { get; } = path;

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}

/// <summary>The seam over the dump of Aethera's own database that is uploaded to a backup destination (ADR 0010), so tests need no <c>pg_dump</c>.</summary>
public interface IControlPlaneDump
{
    /// <exception cref="BackupFailure"><c>pg_dump</c> is missing or failed.</exception>
    Task<ControlPlaneDumpFile> CreateAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The same dump as the daily local backup of the maintenance pass (<see cref="DatabaseBackup"/>), written to a temporary folder instead of the backup directory.</summary>
public sealed class PgDumpControlPlaneDump(IConfiguration configuration, ILogger<PgDumpControlPlaneDump> logger) : IControlPlaneDump
{
    public async Task<ControlPlaneDumpFile> CreateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString)) throw new BackupFailure("controlplane.no_database", "No database connection string is configured.");

        var directory = Path.Combine(Path.GetTempPath(), "aethera-offsite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var backup = new DatabaseBackup(connectionString, directory, logger);
        try
        {
            if (!backup.IsAvailable()) throw new BackupFailure("controlplane.pg_dump_missing", "pg_dump is not installed next to the control plane.");
            var file = await backup.BackupAsync("offsite", now, cancellationToken);
            return new ControlPlaneDumpFile(directory, Path.Combine(directory, file.Name));
        }
        catch (BackupFailedException ex)
        {
            throw new BackupFailure("controlplane.dump_failed", ex.Message, inner: ex);
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            throw;
        }
    }
}
