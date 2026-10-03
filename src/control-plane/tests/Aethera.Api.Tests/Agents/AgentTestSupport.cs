using System.Threading.Channels;
using Aethera.Agent.V1;
using Aethera.Domain;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Crypto;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DomainDockerStatus = Aethera.Domain.DockerStatus;
using ProtoDockerStatus = Aethera.Agent.V1.DockerStatus;

namespace Aethera.Api.Tests.Agents;

/// <summary>
/// The agent gateway services on a real throw-away PostgreSQL database, without ASP.NET: the stream handler, dispatcher, ingestors and CA
/// are driven by a scripted <see cref="FakeAgent"/>. Short timers (heartbeat 0.3 s, ack 0.6 s, grace 0.3 s) keep the tests fast.
/// </summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    private TestDatabase? _database;

    public bool Enabled { get; private set; }

    public ServiceProvider Services { get; private set; } = null!;

    public string ConnectionString => _database?.ConnectionString ?? "";

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        if (_database is null) return;
        Enabled = true;

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Aethera"] = _database.ConnectionString,
            ["Aethera:Jobs:WorkerCount"] = "0",
            ["Aethera:Agents:Enabled"] = "true",
            ["Aethera:Agents:HeartbeatSeconds"] = "0.3",
            ["Aethera:Agents:AckTimeoutSeconds"] = "0.6",
            ["Aethera:Agents:DeadlineGraceSeconds"] = "0.3",
            ["Aethera:Agents:HelloTimeoutSeconds"] = "1",
            ["Aethera:Agents:HeartbeatPersistSeconds"] = "0",
            ["Aethera:Agents:LogInitialWindowBytes"] = "200",
            ["Aethera:Agents:LogChunkMaxBytes"] = "1000",
            ["Aethera:Agents:ReachabilityFailureThreshold"] = "2",
            ["Aethera:Agents:PublicEndpoint"] = "localhost:9443",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<ISecretProtector>(_ => new AesGcmSecretProtector(new MasterKeyring(new Dictionary<int, byte[]> { [1] = MasterKeyring.TestKey }, 1)));
        services.AddAetheraPersistence(configuration);
        services.AddAetheraJobSystem(configuration);
        services.AddAetheraAgents(configuration);
        Services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (Services is not null) await Services.DisposeAsync();
        if (_database is not null) await _database.DisposeAsync();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    /// <summary>A new organization with one server (state <c>pending</c>), as the REST API leaves it before enrollment.</summary>
    public async Task<(Guid OrganizationId, Guid ServerId)> SeedServerAsync(string host = "203.0.113.10", int sshPort = 22, bool withSsh = false)
    {
        return await WithDbAsync(async db =>
        {
            var identity = await TestSeeder.SeedIdentityAsync(db);
            var server = new Server { OrganizationId = identity.OrganizationId, Name = "srv-" + Guid.NewGuid().ToString("N")[..10], Host = host, SshPort = sshPort };
            db.Servers.Add(server);
            await db.SaveChangesAsync();
            if (withSsh)
            {
                var secret = new Secret { OrganizationId = identity.OrganizationId, Name = "ssh-" + Guid.NewGuid().ToString("N")[..8], Purpose = SecretPurpose.SshCredential };
                db.Secrets.Add(secret);
                await db.SaveChangesAsync();
                server.SshCredentialSecretId = secret.Id;
                await db.SaveChangesAsync();
            }

            return (identity.OrganizationId, server.Id);
        });
    }

    public Task<Server> LoadServerAsync(Guid serverId) =>
        WithDbAsync(db => db.Servers.AsNoTracking().FirstAsync(s => s.Id == serverId));

    /// <summary>Polls until <paramref name="condition"/> holds; fails with <paramref name="what"/> after the timeout.</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}");
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Aethera.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class GatewayCollection : ICollectionFixture<GatewayFixture>
{
    public const string Name = "agent-gateway";
}


internal sealed class ChannelStreamReader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
{
    public T Current { get; private set; } = default!;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        try
        {
            if (!await reader.WaitToReadAsync(cancellationToken)) return false;
            if (!reader.TryRead(out var item)) return false;
            Current = item;
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }
}

internal sealed class ChannelStreamWriter<T>(ChannelWriter<T> writer) : IServerStreamWriter<T>
{
    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message) => writer.WriteAsync(message).AsTask();

    public Task WriteAsync(T message, CancellationToken cancellationToken) => writer.WriteAsync(message, cancellationToken).AsTask();
}

/// <summary>
/// A scripted agent on the other end of <see cref="AgentSessionHandler"/>: it plays the agent's side of the protocol through in-memory
/// channels (no TLS, no sockets), which is where the stream logic (liveness, reconciliation, flow control) is exercised.
/// </summary>
public sealed class FakeAgent : IAsyncDisposable
{
    private readonly Channel<AgentMessage> _toServer = Channel.CreateUnbounded<AgentMessage>();
    private readonly Channel<ControlMessage> _fromServer = Channel.CreateUnbounded<ControlMessage>();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<ControlMessage> _backlog = [];
    private Task? _heartbeats;
    private ulong _seq;

    private FakeAgent(Guid serverId, string serial)
    {
        ServerId = serverId;
        Identity = new AgentIdentity(serverId, serial, "00", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
    }

    public Guid ServerId { get; }

    public AgentIdentity Identity { get; }

    public Task Run { get; private set; } = Task.CompletedTask;

    public Welcome? Welcome { get; private set; }

    public static FakeAgent Start(GatewayFixture fixture, Guid serverId, Action<Hello>? configureHello = null, bool heartbeats = true, string? serial = null, bool sendHello = true)
    {
        var agent = new FakeAgent(serverId, serial ?? Guid.NewGuid().ToString("N").ToUpperInvariant());
        var handler = fixture.Get<AgentSessionHandler>();
        agent.Run = Task.Run(() => handler.RunAsync(agent.Identity, new ChannelStreamReader<AgentMessage>(agent._toServer.Reader), new ChannelStreamWriter<ControlMessage>(agent._fromServer.Writer), agent._stop.Token));
        if (sendHello)
        {
            var hello = new Hello { ServerId = serverId.ToString("D"), AgentVersion = "0.1.0", ProtocolVersion = 1, Os = "linux", Architecture = "amd64", ProcessId = "p-" + Guid.NewGuid().ToString("N")[..6] };
            hello.Capabilities.AddRange(["compose.v2", "logs.follow", "build.dockerfile"]);
            configureHello?.Invoke(hello);
            agent.Send(new AgentMessage { Hello = hello });
        }

        if (heartbeats) agent._heartbeats = Task.Run(() => agent.HeartbeatLoopAsync());
        return agent;
    }

    public void Send(AgentMessage message) => _toServer.Writer.TryWrite(message);

    public void Heartbeat(ProtoDockerStatus docker = ProtoDockerStatus.Running) =>
        Send(new AgentMessage { Heartbeat = new Aethera.Agent.V1.Heartbeat { Seq = ++_seq, SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), DockerStatus = docker } });

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Heartbeat();
                await Task.Delay(100, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    public void StopHeartbeats() => _heartbeats = null;

    /// <summary>Closes the agent's side of the stream, as a dropped connection does.</summary>
    public void Drop()
    {
        _stop.Cancel();
        _toServer.Writer.TryComplete();
    }

    /// <summary>The next control message matching <paramref name="match"/>; earlier non-matching ones are kept for later calls.</summary>
    public async Task<T> NextAsync<T>(Func<ControlMessage, T?> pick, int timeoutMs = 5000) where T : class
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            for (var i = 0; i < _backlog.Count; i++)
            {
                if (pick(_backlog[i]) is { } found)
                {
                    _backlog.RemoveAt(i);
                    return found;
                }
            }

            try
            {
                if (!await _fromServer.Reader.WaitToReadAsync(timeout.Token)) throw new Xunit.Sdk.XunitException("The server closed the stream before the expected message.");
                while (_fromServer.Reader.TryRead(out var message)) _backlog.Add(message);
            }
            catch (OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException($"Timed out waiting for a control message (saw: {string.Join(", ", _backlog.Select(m => m.PayloadCase))}).");
            }
        }
    }

    public async Task<Welcome> ExpectWelcomeAsync() => Welcome = await NextAsync(m => m.Welcome);

    public Task<Command> NextCommandAsync(int timeoutMs = 5000) => NextAsync(m => m.Command, timeoutMs);

    public Task<Disconnect> NextDisconnectAsync(int timeoutMs = 5000) => NextAsync(m => m.Disconnect, timeoutMs);

    /// <summary>All control messages received so far that are still unread (for negative assertions).</summary>
    public async Task<IReadOnlyList<ControlMessage>> DrainAsync(int settleMs = 150)
    {
        await Task.Delay(settleMs);
        while (_fromServer.Reader.TryRead(out var message)) _backlog.Add(message);
        return _backlog.ToList();
    }

    public void Ack(string commandId, AckStatus status = AckStatus.Accepted, ErrorCode error = ErrorCode.Unspecified, string message = "") =>
        Send(new AgentMessage { CommandAck = new CommandAck { CommandId = commandId, Status = status, ErrorCode = error, Message = message } });

    public void Result(CommandResult result) => Send(new AgentMessage { CommandResult = result });

    public void Succeed(string commandId, Action<CommandResult>? payload = null)
    {
        var result = new CommandResult { CommandId = commandId, Status = CommandStatus.Succeeded, Empty = new EmptyResult() };
        payload?.Invoke(result);
        Result(result);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _toServer.Writer.TryComplete();
        try { await Run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* the handler ends on its own; a failure here is not what the test is about */ }
        _stop.Dispose();
    }
}
