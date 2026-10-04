using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Protocol;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;
using Aethera.Infrastructure.Ssh.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Ssh;

/// <summary>
/// The fallback transport (ADR 0002 "SshTransport"): typed commands run as allowlisted Docker CLI calls over one multiplexed SSH connection
/// per server. Capabilities are honest: no pushed events or metrics (the poller fills the metrics model instead), builds only for an image
/// pull or a public git URL, no self-update. Registered next to <see cref="Agents.Transport.AgentTransport"/>; the resolver prefers the agent.
/// </summary>
public sealed class SshTransport(
    SshConnectionPool pool, SshHealthProber prober, IOptions<SshOptions> options, IOptions<AgentGatewayOptions> gatewayOptions, ILogSinkFactory logSinks,
    AgentDiscoveryStore discoveryStore, AgentAudit audit, TimeProvider time, ILogger<SshTransport> logger) : IServerTransport
{
    /// <summary>Everything the Docker CLI mapping can carry. <c>PushEvents</c>, <c>PushMetrics</c> and <c>SelfUpdate</c> are deliberately absent.</summary>
    public const TransportCapabilities SshCapabilities =
        TransportCapabilities.ContainerOps | TransportCapabilities.ImageOps | TransportCapabilities.VolumeNetworkOps | TransportCapabilities.Compose
        | TransportCapabilities.Builds | TransportCapabilities.LogFollow | TransportCapabilities.HealthProbe;

    private readonly SshDockerPolicy _policy = options.Value.ToDockerPolicy();

    public TransportKind Kind => TransportKind.Ssh;

    public TransportCapabilities Capabilities => SshCapabilities;

    public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var (available, reason) = pool.Peek(serverId);
        return ValueTask.FromResult(available ? TransportStatus.Up(TransportKind.Ssh) : TransportStatus.Down(TransportKind.Ssh, reason ?? "The SSH connection is not available."));
    }

    public async Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        Guid serverId, TCommand command, CommandOptions commandOptions, CancellationToken cancellationToken)
        where TCommand : IServerCommand<TResult>
    {
        var started = time.GetUtcNow();
        var deadline = commandOptions.Deadline ?? started + command.DefaultTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline > started ? deadline - started : TimeSpan.FromSeconds(1));

        ILogSink? sink = null;
        CommandOutcome<TResult> Outcome(CommandStatus status, TResult? result, CommandErrorCode code, string? message, int exit) =>
            new(status, result, code, message, exit, started, time.GetUtcNow(), Replayed: false);

        try
        {
            using var lease = await pool.AcquireAsync(serverId, timeout.Token);
            sink = await OpenSinkAsync(command, commandOptions, timeout.Token);
            var sequence = 0L;
            var logStream = sink?.StreamId;
            void Emit(string text, bool stderr)
            {
                if (sink is null) return;
                var stream = stderr ? LogStream.Stderr : LogStream.Stdout;
                _ = sink.WriteAsync(stream, text, CancellationToken.None).AsTask();
                commandOptions.OnLog?.Invoke(new LogEntry(logStream!, Interlocked.Increment(ref sequence), time.GetUtcNow(), IsBuild(command) ? LogSource.Build : LogSource.Deploy, stream, text));
            }

            Progress(commandOptions, command, "started", 0);
            object result;
            try
            {
                result = command is DiscoveryRefreshCommand
                    ? await DiscoverAsync(serverId, lease, new SshCommandRunner(lease.Connection, _policy, prober, Emit, time, lease.Access.Auth.SecretValues()), timeout.Token)
                    : await new SshCommandRunner(lease.Connection, _policy, prober, Emit, time, lease.Access.Auth.SecretValues()).RunAsync(command, timeout.Token);
            }
            catch (Exception ex) when (IsConnectionLoss(ex))
            {
                lease.MarkBroken();
                throw new ServerTransportException(TransportErrors.Unreachable, "The SSH connection to the server was lost.", inner: ex);
            }

            Progress(commandOptions, command, "finished", 100);
            var outcome = Outcome(CommandStatus.Succeeded, (TResult)result, CommandErrorCode.None, null, 0);
            await AuditAsync(serverId, command.Name, command.ReadOnly, commandOptions, outcome.Status, CommandErrorCode.None, started);
            return outcome;
        }
        catch (SshCommandFailure failure)
        {
            logger.LogDebug("SSH command {Command} failed with {Code}", command.Name, failure.Code);
            await AuditAsync(serverId, command.Name, command.ReadOnly, commandOptions, CommandStatus.Failed, failure.Code, started);
            return Outcome(CommandStatus.Failed, default, failure.Code, failure.Message, failure.ExitStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(serverId, command.Name, command.ReadOnly, commandOptions, CommandStatus.Cancelled, CommandErrorCode.Cancelled, started);
            return Outcome(CommandStatus.Cancelled, default, CommandErrorCode.Cancelled, "The command was cancelled.", -1);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            await AuditAsync(serverId, command.Name, command.ReadOnly, commandOptions, CommandStatus.TimedOut, CommandErrorCode.Timeout, started);
            return Outcome(CommandStatus.TimedOut, default, CommandErrorCode.Timeout, "The command did not finish in time.", -1);
        }
        catch (ServerTransportException ex) when (ex.Code is TransportErrors.CommandRejected)
        {
            await AuditAsync(serverId, command.Name, command.ReadOnly, commandOptions, CommandStatus.Failed, CommandErrorCode.InvalidArgument, started);
            return Outcome(CommandStatus.Failed, default, CommandErrorCode.InvalidArgument, ex.Message, -1);
        }
        finally
        {
            if (sink is not null) await sink.DisposeAsync();
        }
    }

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = DockerCommands.Logs(request);
        using var lease = await pool.AcquireAsync(serverId, cancellationToken, countChannel: false);
        var streamId = "container:" + Guid.CreateVersion7().ToString("N");
        long sequence = 0;
        string? ended = null;

        var lines = lease.Connection.StreamAsync(command, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await lines.MoveNextAsync();
                }
                catch (Exception ex) when (IsConnectionLoss(ex))
                {
                    lease.MarkBroken();
                    ended = "The SSH connection was lost.";
                    break;
                }

                if (!hasNext) break;
                var line = lines.Current;
                if (line.IsStderr ? !request.IncludeStderr : !request.IncludeStdout) continue;
                var (timestamp, text) = SplitTimestamp(line.Text, time.GetUtcNow());
                yield return new LogEntry(streamId, ++sequence, timestamp, LogSource.Container, line.IsStderr ? LogStream.Stderr : LogStream.Stdout, text);
            }
        }
        finally
        {
            await lines.DisposeAsync();
        }

        yield return new LogEntry(streamId, ++sequence, time.GetUtcNow(), LogSource.Container, LogStream.Stdout, "", Eof: true, EofReason: ended ?? "end of log");
    }

    /// <summary>The SSH transport pushes nothing: container events are not observable without the agent (the stream ends at once).</summary>
    public async IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary><c>docker logs --timestamps</c> prefixes every line with an RFC 3339 timestamp and a space.</summary>
    public static (DateTimeOffset Timestamp, string Text) SplitTimestamp(string line, DateTimeOffset fallback)
    {
        var space = line.IndexOf(' ');
        if (space is > 19 and < 40 && line[4] == '-' && line[10] == 'T')
        {
            var stamp = line[..space];
            var dot = stamp.IndexOf('.');
            var zone = stamp.IndexOfAny(['Z', '+', '-'], Math.Max(dot, 19));
            if (dot > 0 && zone > dot && zone - dot - 1 > 7) stamp = stamp[..(dot + 8)] + stamp[zone..]; // keep 7 fractional digits
            if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return (parsed.ToUniversalTime(), line[(space + 1)..]);
        }

        return (fallback, line);
    }

    private static bool IsConnectionLoss(Exception ex) =>
        ex is Renci.SshNet.Common.SshConnectionException or Renci.SshNet.Common.SshException or SocketException or IOException or ObjectDisposedException
        || ex is InvalidOperationException { Message: var m } && m.Contains("not connected", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuild<TCommand>(TCommand command) => command is BuildImageCommand;

    private async Task<ILogSink?> OpenSinkAsync<TCommand>(TCommand command, CommandOptions commandOptions, CancellationToken cancellationToken)
    {
        var streamId = commandOptions.LogStreamId ?? (command is BuildImageCommand build ? $"build:{build.Spec.BuildId}" : null);
        if (streamId is null) return null;
        return await logSinks.CreateAsync(streamId, new SecretRedactor(), command is BuildImageCommand ? LogSource.Build : LogSource.Deploy, cancellationToken);
    }

    private static void Progress<TCommand>(CommandOptions commandOptions, TCommand command, string step, int percent)
    {
        if (commandOptions.OnProgress is null || command is not (ImagePullCommand or ComposeUpCommand or ComposePullCommand or ComposeDownCommand or BuildImageCommand or ContainerCreateCommand)) return;
        commandOptions.OnProgress(new CommandProgressInfo(step, step, percent, DateTimeOffset.UtcNow));
    }

    private async Task AuditAsync(Guid serverId, string name, bool readOnly, CommandOptions commandOptions, CommandStatus status, CommandErrorCode code, DateTimeOffset started)
    {
        // Same rule as the agent path: command name, ids and outcome only; never a payload or a command line.
        if (readOnly && !gatewayOptions.Value.AuditReadOnlyCommands) return;
        await audit.RecordAsync("ssh.command", "server", serverId,
            new
            {
                command = name, idempotency = commandOptions.IdempotencyKey, jobId = commandOptions.JobId, transport = "ssh",
                outcome = status.ToString().ToLowerInvariant(), errorCode = code.ToString(), durationMs = (long)(time.GetUtcNow() - started).TotalMilliseconds,
            },
            organizationId: commandOptions.OrganizationId, actorUserId: commandOptions.ActorUserId);
    }

    // ---- discovery -----------------------------------------------------------------------------------------------------------------

    private async Task<object> DiscoverAsync(Guid serverId, SshLease lease, SshCommandRunner runner, CancellationToken cancellationToken)
    {
        var result = await lease.Connection.ExecuteAsync(new RemoteCommand(SshDiscoveryScript.Line), new RemoteExecOptions { Timeout = TimeSpan.FromSeconds(45) }, cancellationToken);
        if (!result.Succeeded) throw new SshCommandFailure(CommandErrorCode.Internal, DockerErrors.Summarize(result.Stderr, result.Stdout), result.ExitStatus);
        var report = SshDiscoveryScript.Parse(result.Stdout, time.GetUtcNow());
        await discoveryStore.SaveAsync(serverId, report, cancellationToken); // facts, Docker axis and the stored report, as for an agent's report

        var info = report.ToDomain();
        if (info.Docker.Status != DockerStatus.Running) return info;
        var containers = (IReadOnlyList<DockerContainer>)await runner.RunAsync(new ContainerListCommand(), cancellationToken);
        var networks = (IReadOnlyList<DockerNetwork>)await runner.RunAsync(new NetworkListCommand(), cancellationToken);
        var volumes = (IReadOnlyList<DockerVolume>)await runner.RunAsync(new VolumeListCommand(), cancellationToken);
        return info with { Containers = containers, Networks = networks, Volumes = volumes };
    }
}
