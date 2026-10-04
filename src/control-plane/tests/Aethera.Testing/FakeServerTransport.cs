using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aethera.Domain.Transport;

namespace Aethera.Testing;

/// <summary>A command the fake received.</summary>
public sealed record RecordedCommand(Guid ServerId, object Command, CommandOptions Options);

/// <summary>
/// An <see cref="IServerTransport"/> for tests (ADR 0002): no Docker daemon, agent or network needed. Script it per command type with
/// <see cref="On{TCommand, TResult}"/> or <see cref="Fail{TCommand, TResult}"/>; every call is recorded in <see cref="Commands"/>. A command
/// without a script throws, so a test never silently passes through an unexpected call.
/// </summary>
public sealed class FakeServerTransport : IServerTransport
{
    private readonly Dictionary<Type, Func<object, CommandOptions, object>> _handlers = [];
    private readonly Channel<ServerEvent> _events = Channel.CreateUnbounded<ServerEvent>();
    private readonly List<LogEntry> _logScript = [];

    public TransportKind Kind { get; set; } = TransportKind.Agent;

    public TransportCapabilities Capabilities { get; set; } = TransportCapabilities.AllAgent;

    /// <summary>When false, <see cref="GetStatusAsync"/> reports down and <see cref="ExecuteAsync{TCommand, TResult}"/> throws <c>server.agent_unavailable</c>.</summary>
    public bool Available { get; set; } = true;

    public List<RecordedCommand> Commands { get; } = [];

    /// <summary>Scripts the result of a command type.</summary>
    public FakeServerTransport On<TCommand, TResult>(Func<TCommand, TResult> handler) where TCommand : IServerCommand<TResult>
    {
        _handlers[typeof(TCommand)] = (command, _) => new CommandOutcome<TResult>(
            CommandStatus.Succeeded, handler((TCommand)command), CommandErrorCode.None, null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);
        return this;
    }

    public FakeServerTransport On<TCommand, TResult>(TResult result) where TCommand : IServerCommand<TResult> => On<TCommand, TResult>(_ => result);

    /// <summary>Scripts a command that ran and failed on the host.</summary>
    public FakeServerTransport Fail<TCommand, TResult>(CommandErrorCode code, string message) where TCommand : IServerCommand<TResult>
    {
        _handlers[typeof(TCommand)] = (_, _) => new CommandOutcome<TResult>(CommandStatus.Failed, default, code, message, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);
        return this;
    }

    /// <summary>Scripts a transport-level failure (agent unavailable, ack timeout...).</summary>
    public FakeServerTransport Throw<TCommand>(ServerTransportException exception)
    {
        _handlers[typeof(TCommand)] = (_, _) => throw exception;
        return this;
    }

    public FakeServerTransport LogLines(params LogEntry[] entries)
    {
        _logScript.AddRange(entries);
        return this;
    }

    public void Raise(ServerEvent serverEvent) => _events.Writer.TryWrite(serverEvent);

    public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Available ? TransportStatus.Up(Kind) : TransportStatus.Down(Kind, "The fake transport is down."));

    public Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(Guid serverId, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : IServerCommand<TResult>
    {
        lock (Commands) Commands.Add(new RecordedCommand(serverId, command, options));
        if (!Available) throw new ServerTransportException(TransportErrors.AgentUnavailable, "The fake transport is down.");
        if (!_handlers.TryGetValue(typeof(TCommand), out var handler))
            throw new InvalidOperationException($"FakeServerTransport has no script for {typeof(TCommand).Name}.");
        return Task.FromResult((CommandOutcome<TResult>)handler(command, options));
    }

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var entry in _logScript)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken)) yield return item;
    }

    /// <summary>The recorded commands of a type.</summary>
    public IReadOnlyList<TCommand> Received<TCommand>() where TCommand : class
    {
        lock (Commands) return Commands.Select(c => c.Command).OfType<TCommand>().ToList();
    }
}
