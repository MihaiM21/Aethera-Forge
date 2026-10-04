using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Crypto;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aethera.Api.Tests.Ssh;

/// <summary>Skips a test unless <c>AETHERA_TEST_SSH</c> (host:port of the throw-away sshd of <c>Ssh/sshd</c>) is set.</summary>
public sealed class RequiresSshdFactAttribute : FactAttribute
{
    public RequiresSshdFactAttribute()
    {
        if (!SshdEndpoint.IsConfigured)
            Skip = "AETHERA_TEST_SSH is not set; build and start tests/Aethera.Api.Tests/Ssh/sshd (see its Dockerfile) and point AETHERA_TEST_SSH at it, e.g. 127.0.0.1:15250.";
    }
}

/// <summary>Where the test sshd listens, from the environment, and a helper to run <c>docker exec</c> in it.</summary>
public static class SshdEndpoint
{
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AETHERA_TEST_SSH"));

    public static string Host => Environment.GetEnvironmentVariable("AETHERA_TEST_SSH")!.Split(':')[0];

    public static int Port => int.Parse(Environment.GetEnvironmentVariable("AETHERA_TEST_SSH")!.Split(':')[1]);

    /// <summary>Docker container name of the sshd (needed to rotate its host key); optional.</summary>
    public static string? Container => Environment.GetEnvironmentVariable("AETHERA_TEST_SSH_CONTAINER");

    public const string Password = "aethera-test"; // a throw-away value baked into the test image, not a secret

    public static SshTarget Target(string user = "root") => new(Host, Port, user);

    public static SshConnectionSettings Settings => new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

    public static async Task<string> DockerExecAsync(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(Container ?? throw new InvalidOperationException("AETHERA_TEST_SSH_CONTAINER is not set."));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}

/// <summary>A scripted SSH session: records every command, upload and tunnel and answers from a handler.</summary>
public sealed class FakeSshConnection(string fingerprint = "SHA256:" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") : ISshConnection
{
    private int _active;
    private int _maxActive;

    public bool IsConnected { get; set; } = true;

    public HostKeyInfo HostKey { get; } = new("ssh-ed25519", fingerprint);

    public ConcurrentQueue<RemoteCommand> Commands { get; } = new();

    public ConcurrentQueue<(string Path, string Content, string Mode)> Uploads { get; } = new();

    public ConcurrentQueue<(string Host, int Port)> Tunnels { get; } = new();

    public int MaxConcurrent => _maxActive;

    public TimeSpan Delay { get; set; }

    /// <summary>Answer for a command; the default succeeds with empty output.</summary>
    public Func<RemoteCommand, RemoteResult> Handler { get; set; } = _ => new RemoteResult(0, "", "");

    public Func<string, int, Stream>? TunnelHandler { get; set; }

    public Func<RemoteCommand, IEnumerable<RemoteLine>>? StreamHandler { get; set; }

    public async Task<RemoteResult> ExecuteAsync(RemoteCommand command, RemoteExecOptions options, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        int seen;
        while (active > (seen = _maxActive)) if (Interlocked.CompareExchange(ref _maxActive, active, seen) == seen) break;
        try
        {
            Commands.Enqueue(command);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            var result = Handler(command);
            if (options.OnLine is { } onLine)
            {
                foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onLine(new RemoteLine(false, line));
                foreach (var line in result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onLine(new RemoteLine(true, line));
            }

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public async IAsyncEnumerable<RemoteLine> StreamAsync(RemoteCommand command, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Commands.Enqueue(command);
        await Task.Yield();
        foreach (var line in StreamHandler?.Invoke(command) ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    public async Task UploadAsync(string remotePath, Stream content, string mode, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Encoding.UTF8);
        Uploads.Enqueue((remotePath, await reader.ReadToEndAsync(cancellationToken), mode));
    }

    public Task<Stream> OpenTunnelAsync(string host, int port, CancellationToken cancellationToken)
    {
        Tunnels.Enqueue((host, port));
        return Task.FromResult(TunnelHandler?.Invoke(host, port) ?? throw new InvalidOperationException("No tunnel scripted."));
    }

    public string[] Lines() => Commands.Select(c => c.Line).ToArray();

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="ISshConnector"/> that hands out <see cref="FakeSshConnection"/>s and enforces the pinned host key like the real one.</summary>
public sealed class FakeSshConnector : ISshConnector
{
    public FakeSshConnection Connection { get; set; } = new();

    public int Connects;

    public List<(SshTarget Target, SshAuth Auth, string? Expected)> Attempts { get; } = [];

    /// <summary>When set, connecting throws this instead.</summary>
    public Exception? Failure { get; set; }

    public TimeSpan ConnectDelay { get; set; }

    public async Task<ISshConnection> ConnectAsync(SshTarget target, SshAuth auth, string? expectedFingerprint, SshConnectionSettings settings, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Connects);
        lock (Attempts) Attempts.Add((target, auth, expectedFingerprint));
        if (ConnectDelay > TimeSpan.Zero) await Task.Delay(ConnectDelay, cancellationToken);
        if (Failure is not null) throw Failure;
        if (expectedFingerprint is not null && expectedFingerprint != Connection.HostKey.Fingerprint)
            throw new SshHostKeyChangedException(expectedFingerprint, Connection.HostKey);
        Connection.IsConnected = true;
        return Connection;
    }

    public Task<HostKeyInfo> ScanHostKeyAsync(string host, int port, SshConnectionSettings settings, CancellationToken cancellationToken) => Task.FromResult(Connection.HostKey);
}

/// <summary>Collects every log message of a test host so tests can assert that no secret was written.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Messages.Enqueue($"{logLevel} {formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// The SSH services on a real throw-away PostgreSQL database without ASP.NET: pool, host key service, transport, poller, bootstrap and
/// resolver, with a scripted or the real SSH connector. Short timers keep tests fast.
/// </summary>
public sealed class SshTestHost : IAsyncDisposable
{
    private TestDatabase? _database;
    private LogIngestor? _logIngestor;

    public ServiceProvider Services { get; private set; } = null!;

    public CapturingLoggerProvider Logs { get; } = new();

    public FakeSshConnector? Connector { get; private set; }

    public static async Task<SshTestHost?> CreateAsync(bool fakeConnector = true, Action<IServiceCollection>? customize = null, IDictionary<string, string?>? settings = null)
    {
        var host = new SshTestHost();
        host._database = await TestDatabase.CreateAsync();
        if (host._database is null) return null;

        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Aethera"] = host._database.ConnectionString,
            ["Aethera:Jobs:WorkerCount"] = "0",
            ["Aethera:Agents:Enabled"] = "true",
            ["Aethera:Agents:PublicEndpoint"] = "aethera.example.com:9443",
            ["Aethera:Ssh:Enabled"] = "false", // the poller is driven by the tests
            ["Aethera:Ssh:ConnectFailureBackoffSeconds"] = "0.5",
            ["Aethera:Ssh:MaxConcurrentChannelsPerServer"] = "3",
            ["Aethera:Ssh:AccessCacheSeconds"] = "0",
            ["Aethera:Ssh:BootstrapSessionWaitSeconds"] = "2",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(host.Logs));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<ISecretProtector>(_ => new AesGcmSecretProtector(new MasterKeyring(new Dictionary<int, byte[]> { [1] = MasterKeyring.TestKey }, 1)));
        if (fakeConnector)
        {
            host.Connector = new FakeSshConnector();
            services.AddSingleton<ISshConnector>(host.Connector);
        }

        customize?.Invoke(services);
        services.AddAetheraPersistence(configuration);
        services.AddAetheraJobSystem(configuration);
        services.AddAetheraAgents(configuration);
        host.Services = services.BuildServiceProvider();
        host._logIngestor = host.Services.GetRequiredService<LogIngestor>(); // the hosted services are not started here: log sinks need the ingestor
        await host._logIngestor.StartAsync(CancellationToken.None);
        return host;
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public SshTransport Transport => Services.GetServices<IServerTransport>().OfType<SshTransport>().Single();

    public async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    /// <summary>A server with an SSH credential secret holding <paramref name="credential"/> (a password, a PEM key or the JSON form).</summary>
    public async Task<Guid> SeedServerAsync(string credential = "pw-hunter2", string host = "203.0.113.20", int port = 22, string user = "root", string? pinned = null, bool withCredential = true)
    {
        return await WithDbAsync(async db =>
        {
            var identity = await TestSeeder.SeedIdentityAsync(db);
            var server = new Server
            {
                OrganizationId = identity.OrganizationId, Name = "srv-" + Guid.NewGuid().ToString("N")[..10], Host = host, SshPort = port, SshUser = user,
                Transport = ServerTransport.Ssh, SshHostKeyFingerprint = pinned,
            };
            db.Servers.Add(server);
            if (withCredential)
            {
                var vault = new SecretVault(db, Get<ISecretProtector>(), Get<IClock>());
                var secret = vault.Create(identity.OrganizationId, "ssh/" + server.Name, null, credential, purpose: SecretPurpose.SshCredential);
                server.SshCredentialSecretId = secret.Id;
            }

            await db.SaveChangesAsync();
            return server.Id;
        });
    }

    public Task<Server> LoadServerAsync(Guid id) => WithDbAsync(db => db.Servers.AsNoTracking().FirstAsync(s => s.Id == id));

    public async ValueTask DisposeAsync()
    {
        if (_logIngestor is not null) await _logIngestor.StopAsync(CancellationToken.None);
        if (Services is not null) await Services.DisposeAsync();
        if (_database is not null) await _database.DisposeAsync();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Aethera.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

/// <summary>A generated test key pair: a PEM private key and its OpenSSH public key line.</summary>
public static class TestKeys
{
    public static (string PrivatePem, string AuthorizedKeysLine) NewRsa()
    {
        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportRSAPrivateKeyPem(); // -----BEGIN RSA PRIVATE KEY-----
        var parameters = rsa.ExportParameters(false);
        using var blob = new MemoryStream();
        void Write(byte[] data)
        {
            var length = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(length);
            blob.Write(length);
            blob.Write(data);
        }

        byte[] MPint(byte[] value)
        {
            var trimmed = value.SkipWhile(b => b == 0).ToArray();
            return trimmed.Length > 0 && trimmed[0] >= 0x80 ? [0, .. trimmed] : trimmed;
        }

        Write(Encoding.ASCII.GetBytes("ssh-rsa"));
        Write(MPint(parameters.Exponent!));
        Write(MPint(parameters.Modulus!));
        return (pem, "ssh-rsa " + Convert.ToBase64String(blob.ToArray()) + " aethera-test");
    }
}
