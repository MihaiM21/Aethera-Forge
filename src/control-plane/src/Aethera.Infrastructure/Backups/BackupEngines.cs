using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Backups;

/// <summary>A named volume (or bind mount) of the workload, as an engine sees it.</summary>
public sealed record BackupVolumeInfo(string Name, string MountPath, bool IsBind);

/// <summary>
/// Everything an engine needs to build its commands. <see cref="Vars"/> are the workload's environment variables with secret-backed ones already
/// resolved; the engine decides which of them travel as secrets.
/// </summary>
/// <param name="Database">Overrides the database a PostgreSQL service dumps (default: <c>POSTGRES_DB</c>).</param>
public sealed record BackupEngineContext(string Container, IReadOnlyDictionary<string, string> Vars, string? Database, IReadOnlyList<BackupVolumeInfo> Volumes);

/// <summary>A database engine's dump and restore programs (ADR 0010). Pure: the builders only assemble argument vectors and environments.</summary>
public interface IBackupEngine
{
    /// <summary><c>postgres</c>, <c>mongodb</c>, <c>redis</c>, <c>mysql</c> (also MariaDB): stored as <c>backup_runs.engine</c>.</summary>
    string Name { get; }

    /// <summary>File extension of the stored object before <c>.aeb</c>.</summary>
    string Extension { get; }

    /// <summary>True when the workload must be stopped while the stream is restored (the restore replaces files, not rows).</summary>
    bool StopsForRestore { get; }

    BackupSourceSpec Dump(BackupEngineContext context);

    BackupSinkSpec Restore(BackupEngineContext context);
}

/// <summary>Raised when a policy cannot be turned into commands (no data volume, a bind mount...). The message is shown to the user.</summary>
public sealed class BackupEngineException(string message) : Exception(message);

/// <summary>
/// The engines of ADR 0010, chosen by the template of a service. Passwords reach the programs through the environment of the exec (<c>PGPASSWORD</c>,
/// <c>MYSQL_PWD</c>, <c>REDISCLI_AUTH</c>) as secret variables and never appear in an argument vector. <c>mongodump</c> and <c>mongorestore</c> take a
/// password only on the command line or from a config file, so MongoDB goes through a short script that writes a 0600 config file inside the
/// container and removes it afterwards (see <see cref="MongoScript"/>).
/// </summary>
public static class BackupEngines
{
    public static IBackupEngine? ForTemplate(string? templateKey) => templateKey?.Trim().ToLowerInvariant() switch
    {
        "postgres" => new PostgresEngine(),
        "mysql" => new MySqlEngine(mariaDb: false),
        "mariadb" => new MySqlEngine(mariaDb: true),
        "mongodb" => new MongoEngine(),
        "redis" => new RedisEngine(),
        _ => null,
    };

    public static bool Supports(string? templateKey) => ForTemplate(templateKey) is not null;

    /// <summary>Templates that have a database policy, for the API and the UI.</summary>
    public static IReadOnlyList<string> SupportedTemplates { get; } = ["postgres", "mysql", "mariadb", "mongodb", "redis"];

    private static string Var(BackupEngineContext c, string name) => c.Vars.TryGetValue(name, out var v) ? v : "";

    private static EnvVarSpec[] Secrets(params (string Name, string Value)[] vars) =>
        vars.Where(v => v.Value.Length > 0).Select(v => EnvVarSpec.OfSecret(v.Name, v.Value)).ToArray();

    private sealed class PostgresEngine : IBackupEngine
    {
        public string Name => "postgres";
        public string Extension => "pgdump";
        public bool StopsForRestore => false;

        private static (string User, string Database) Target(BackupEngineContext c)
        {
            var user = Var(c, "POSTGRES_USER") is { Length: > 0 } u ? u : "postgres";
            var database = c.Database is { Length: > 0 } db ? db : Var(c, "POSTGRES_DB") is { Length: > 0 } d ? d : user;
            return (user, database);
        }

        public BackupSourceSpec Dump(BackupEngineContext c)
        {
            var (user, database) = Target(c);
            return new ExecDumpSource(c.Container, ["pg_dump", "-Fc", "-U", user, "-d", database], Secrets(("PGPASSWORD", Var(c, "POSTGRES_PASSWORD"))));
        }

        public BackupSinkSpec Restore(BackupEngineContext c)
        {
            var (user, database) = Target(c);
            return new ExecRestoreSink(c.Container, ["pg_restore", "--clean", "--if-exists", "--no-owner", "-U", user, "-d", database],
                Secrets(("PGPASSWORD", Var(c, "POSTGRES_PASSWORD"))));
        }
    }

    private sealed class MySqlEngine(bool mariaDb) : IBackupEngine
    {
        public string Name => "mysql";
        public string Extension => "sql";
        public bool StopsForRestore => false;

        private string Password(BackupEngineContext c) =>
            Var(c, mariaDb ? "MARIADB_ROOT_PASSWORD" : "MYSQL_ROOT_PASSWORD") is { Length: > 0 } own ? own : Var(c, mariaDb ? "MYSQL_ROOT_PASSWORD" : "MARIADB_ROOT_PASSWORD");

        public BackupSourceSpec Dump(BackupEngineContext c) =>
            new ExecDumpSource(c.Container,
                [mariaDb ? "mariadb-dump" : "mysqldump", "--single-transaction", "--routines", "--events", "--all-databases", "-u", "root"],
                Secrets(("MYSQL_PWD", Password(c))));

        public BackupSinkSpec Restore(BackupEngineContext c) =>
            new ExecRestoreSink(c.Container, [mariaDb ? "mariadb" : "mysql", "-u", "root"], Secrets(("MYSQL_PWD", Password(c))));
    }

    private sealed class MongoEngine : IBackupEngine
    {
        public string Name => "mongodb";
        public string Extension => "mongo.gz";
        public bool StopsForRestore => false;

        private static EnvVarSpec[] Credentials(BackupEngineContext c) =>
        [
            EnvVarSpec.OfPlain("AETHERA_DB_USER", Var(c, "MONGO_INITDB_ROOT_USERNAME")),
            EnvVarSpec.OfSecret("AETHERA_DB_PASSWORD", Var(c, "MONGO_INITDB_ROOT_PASSWORD")),
        ];

        private static bool HasAuth(BackupEngineContext c) => Var(c, "MONGO_INITDB_ROOT_USERNAME").Length > 0 && Var(c, "MONGO_INITDB_ROOT_PASSWORD").Length > 0;

        public BackupSourceSpec Dump(BackupEngineContext c) =>
            HasAuth(c)
                ? new ExecDumpSource(c.Container, ["sh", "-c", MongoScript("mongodump", "")], Credentials(c))
                : new ExecDumpSource(c.Container, ["mongodump", "--archive", "--gzip"]);

        public BackupSinkSpec Restore(BackupEngineContext c) =>
            HasAuth(c)
                ? new ExecRestoreSink(c.Container, ["sh", "-c", MongoScript("mongorestore", " --drop")], Credentials(c))
                : new ExecRestoreSink(c.Container, ["mongorestore", "--archive", "--gzip", "--drop"]);
    }

    /// <summary>
    /// Runs <paramref name="program"/> with the password in a config file (<c>password: "..."</c>, YAML-escaped) created with <c>umask 077</c> and
    /// deleted on exit. The script is constant text: user and password come from the environment of the exec, so nothing a user typed is ever
    /// interpolated into it.
    /// </summary>
    internal static string MongoScript(string program, string extraArgs) =>
        "set -eu\n"
        + "umask 077\n"
        + "cfg=$(mktemp)\n"
        + "trap 'rm -f \"$cfg\"' EXIT\n"
        + "esc=$(printf '%s' \"$AETHERA_DB_PASSWORD\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n"
        + "printf 'password: \"%s\"\\n' \"$esc\" > \"$cfg\"\n"
        + $"{program} --config \"$cfg\" --username \"$AETHERA_DB_USER\" --authenticationDatabase admin --archive --gzip{extraArgs}\n";

    private sealed class RedisEngine : IBackupEngine
    {
        public string Name => "redis";

        /// <summary>The stream is a gzipped tar holding <c>dump.rdb</c>, so the generic volume restore of the agent can put it back.</summary>
        public string Extension => "rdb.tar.gz";

        public bool StopsForRestore => true;

        public BackupSourceSpec Dump(BackupEngineContext c) =>
            new ExecDumpSource(c.Container, ["sh", "-c", RedisScript], Secrets(("REDISCLI_AUTH", Var(c, "REDIS_PASSWORD"))));

        public BackupSinkSpec Restore(BackupEngineContext c)
        {
            var data = c.Volumes.FirstOrDefault(v => !v.IsBind && v.MountPath.TrimEnd('/') == "/data")
                       ?? throw new BackupEngineException("The Redis service has no named volume at /data, so there is nothing to restore into.");
            // Cleared first: with AOF persistence Redis loads appendonlydir/ at start and would ignore the restored dump.rdb.
            return new VolumeRestoreSink(data.Name, ClearFirst: true);
        }
    }

    /// <summary>
    /// Asks Redis for an RDB snapshot over the replication protocol (<c>redis-cli --rdb</c>, no file of the server is touched) and streams it as a tar.gz.
    /// The password variable of the template is only used when the server really asks for one.
    /// </summary>
    internal const string RedisScript =
        "set -eu\n"
        + "d=$(mktemp -d)\n"
        + "trap 'rm -rf \"$d\"' EXIT\n"
        + "if env -u REDISCLI_AUTH redis-cli ping 2>/dev/null | grep -q PONG; then unset REDISCLI_AUTH; fi\n"
        + "redis-cli --rdb \"$d/dump.rdb\" >&2\n"
        + "tar -C \"$d\" -czf - dump.rdb\n";
}
