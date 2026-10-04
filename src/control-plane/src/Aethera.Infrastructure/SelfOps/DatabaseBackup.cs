using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aethera.Infrastructure.SelfOps;

/// <summary>A backup file on disk.</summary>
public sealed record BackupFile(string Name, long SizeBytes, DateTimeOffset CreatedAt, string Kind);

/// <summary>The seam over <c>pg_dump</c>, so migration and maintenance logic is testable without it.</summary>
public interface IDatabaseBackup
{
    bool IsAvailable();
    Task<BackupFile> BackupAsync(string kind, DateTimeOffset now, CancellationToken cancellationToken);
    IReadOnlyList<BackupFile> List();
    int Prune(string kind, int keep);
}

public class BackupFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Logical backups of the Aethera database with <c>pg_dump -Fc</c> (restore with <c>pg_restore</c>). The password reaches the child
/// process through <c>PGPASSWORD</c>, never on the command line; the dump is written to a temporary name and renamed when complete, so a
/// crash never leaves something that looks like a backup. Files are named <c>aethera-&lt;kind&gt;-&lt;utc timestamp&gt;.dump</c>.
/// </summary>
public sealed class DatabaseBackup(string connectionString, string directory, ILogger logger, string pgDump = "pg_dump") : IDatabaseBackup
{
    private const string Extension = ".dump";

    public string Directory { get; } = directory;

    /// <summary>True when <c>pg_dump</c> can be started (the container image ships it; a developer machine may not).</summary>
    public bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(pgDump, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null) return false;
            if (!process.WaitForExit(5000)) { process.Kill(); return false; }
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Writes a backup and returns it. <paramref name="kind"/> is <c>scheduled</c>, <c>pre-migration</c> or <c>manual</c>.</summary>
    public async Task<BackupFile> BackupAsync(string kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var name = $"aethera-{kind}-{now.UtcDateTime:yyyyMMdd-HHmmss}{Extension}";
        var path = Path.Combine(Directory, name);
        var temp = path + ".partial";

        var start = new ProcessStartInfo(pgDump) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "--format=custom", "--no-owner", "--file", temp, "--host", builder.Host ?? "localhost", "--port", builder.Port.ToString(), "--username", builder.Username ?? "aethera", "--dbname", builder.Database ?? "aethera" })
            start.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(builder.Password)) start.Environment["PGPASSWORD"] = builder.Password;

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            throw new BackupFailedException("pg_dump is not installed next to the control plane.", ex);
        }
        if (process is null) throw new BackupFailedException("pg_dump could not be started.");
        using (process)
        {
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                TryDelete(temp);
                throw;
            }
            await output;
            if (process.ExitCode != 0)
            {
                TryDelete(temp);
                // pg_dump's own message (host, database, reason), never the password: it is not on the command line.
                throw new BackupFailedException($"pg_dump failed: {(await error).Trim()}");
            }
        }

        File.Move(temp, path, overwrite: true);
        var info = new FileInfo(path);
        logger.LogInformation("Database backup {Name} written ({Bytes} bytes)", name, info.Length);
        return new BackupFile(name, info.Length, now, kind);
    }

    public IReadOnlyList<BackupFile> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return new DirectoryInfo(Directory).EnumerateFiles($"aethera-*{Extension}")
            .Select(f => new BackupFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), KindOf(f.Name)))
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> backups of <paramref name="kind"/>. Returns how many were deleted.</summary>
    public int Prune(string kind, int keep)
    {
        var deleted = 0;
        foreach (var file in List().Where(f => f.Kind == kind).Skip(Math.Max(keep, 1)))
        {
            TryDelete(Path.Combine(Directory, file.Name));
            deleted++;
        }
        return deleted;
    }

    public static string KindOf(string fileName)
    {
        // aethera-<kind>-<yyyyMMdd>-<HHmmss>.dump; the kind itself may contain a dash (pre-migration).
        var parts = fileName[..^Extension.Length].Split('-');
        return parts.Length >= 4 ? string.Join('-', parts[1..^2]) : "unknown";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
