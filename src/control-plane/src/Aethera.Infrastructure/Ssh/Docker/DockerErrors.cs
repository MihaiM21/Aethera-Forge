using System.Globalization;
using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Ssh.Docker;

/// <summary>A remote command ran and failed; carries the stable error code the engine reacts to.</summary>
public sealed class SshCommandFailure(CommandErrorCode code, string message, int exitStatus = 1) : Exception(message)
{
    public CommandErrorCode Code { get; } = code;

    public int ExitStatus { get; } = exitStatus;
}

/// <summary>Turns Docker CLI error output into the protocol's error codes (best effort; the text is Docker's, the code is ours).</summary>
public static class DockerErrors
{
    public static CommandErrorCode Classify(string stderr, int exitStatus, CommandErrorCode fallback)
    {
        var text = stderr;
        bool Has(string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

        if (exitStatus == 127 || Has("docker: command not found") || Has("docker: not found")) return CommandErrorCode.DockerUnavailable;
        if (Has("Cannot connect to the Docker daemon") || Has("Is the docker daemon running")) return CommandErrorCode.DockerUnavailable;
        if (Has("permission denied") && Has("docker.sock")) return CommandErrorCode.PermissionDenied;
        if (Has("no space left on device")) return CommandErrorCode.OutOfDisk;
        if (Has("port is already allocated") || Has("address already in use")) return CommandErrorCode.PortConflict;
        if (Has("No such container") || Has("No such image") || Has("No such volume") || Has("No such network") || Has("no such object") || Has("not found: manifest unknown"))
            return fallback == CommandErrorCode.ImagePullFailed ? CommandErrorCode.ImagePullFailed : CommandErrorCode.NotFound;
        if (Has("is already in use") || Has("already exists")) return CommandErrorCode.AlreadyExists;
        if (Has("unauthorized") || Has("authentication required") || Has("denied: requested access") || Has("incorrect username or password") || Has("pull access denied"))
            return fallback == CommandErrorCode.ImagePullFailed ? CommandErrorCode.RegistryAuthFailed : fallback;
        if (Has("is running") && Has("stop the container before")) return CommandErrorCode.Conflict;
        if (Has("conflict")) return CommandErrorCode.Conflict;
        if (Has("manifest unknown") || Has("repository does not exist") || Has("not found")) return fallback == CommandErrorCode.ImagePullFailed ? CommandErrorCode.ImagePullFailed : CommandErrorCode.NotFound;
        if (Has("invalid reference format") || Has("invalid argument")) return CommandErrorCode.InvalidArgument;
        return fallback;
    }

    /// <summary>The last meaningful lines of stderr (bounded), for the failure message. Secrets must already be masked.</summary>
    public static string Summarize(string stderr, string stdout)
    {
        var source = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var lines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(5);
        var text = string.Join(" | ", lines);
        return text.Length <= 600 ? text : text[..600];
    }

    /// <summary>Parses <c>docker ... prune</c> output into the deleted objects and the reclaimed bytes.</summary>
    public static PruneOutcome ParsePrune(string output)
    {
        var deleted = new List<string>();
        long reclaimed = 0;
        var inList = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { inList = false; continue; }
            if (line.StartsWith("Total reclaimed space:", StringComparison.OrdinalIgnoreCase))
            {
                reclaimed = Metrics.SshMetricsParsers.ParseSize(line["Total reclaimed space:".Length..]);
                inList = false;
            }
            else if (line.StartsWith("Deleted ", StringComparison.Ordinal) && line.EndsWith(':'))
            {
                inList = true;
            }
            else if (inList)
            {
                deleted.Add(line);
            }
            else if (line.StartsWith("deleted: ", StringComparison.Ordinal) || line.StartsWith("untagged: ", StringComparison.Ordinal))
            {
                deleted.Add(line);
            }
        }

        return new PruneOutcome(deleted, reclaimed);
    }

    public static string FormatInvariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
