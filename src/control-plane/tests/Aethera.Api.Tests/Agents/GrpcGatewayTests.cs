using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aethera.Agent.V1;
using Aethera.Api.Features.Agents;
using Aethera.Domain;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Crypto;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Google.Protobuf.WellKnownTypes;

namespace Aethera.Api.Tests.Agents;

/// <summary>
/// The real gateway: Kestrel with the mTLS gRPC listener (TLS 1.3, client certificates validated against the Aethera CA only), the
/// gRPC services and the identity interceptor, on a throw-away database. The agent side is played by <c>Grpc.Net.Client</c>.
/// </summary>
public sealed class GrpcGatewayHost : IAsyncDisposable
{
    private TestDatabase _database = null!;
    private WebApplication _app = null!;

    public int GrpcPort { get; private set; }

    public int HttpPort { get; private set; }

    public IServiceProvider Services => _app.Services;

    public string CaPem { get; private set; } = "";

    public static async Task<GrpcGatewayHost?> StartAsync(int enrollPermits = 1000)
    {
        var database = await TestDatabase.CreateAsync();
        if (database is null) return null;
        var host = new GrpcGatewayHost { _database = database, GrpcPort = FreePort() };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = "http://127.0.0.1:0",
            ["ConnectionStrings:Aethera"] = database.ConnectionString,
            ["Aethera:Jobs:WorkerCount"] = "0",
            ["Aethera:Agents:Enabled"] = "true",
            ["Aethera:Agents:GrpcPort"] = host.GrpcPort.ToString(),
            ["Aethera:Agents:GrpcBindAddress"] = "127.0.0.1",
            ["Aethera:Agents:PublicEndpoint"] = $"localhost:{host.GrpcPort}",
            ["Aethera:Agents:HeartbeatSeconds"] = "0.5",
            ["Aethera:Agents:EnrollRateLimitPermits"] = enrollPermits.ToString(),
            ["Aethera:Agents:Metrics:Enabled"] = "false",
        });
        builder.Services.AddSingleton<ISecretProtector>(_ => new AesGcmSecretProtector(new MasterKeyring(new Dictionary<int, byte[]> { [1] = MasterKeyring.TestKey }, 1)));
        builder.Services.AddAetheraPersistence(builder.Configuration);
        builder.Services.AddAetheraJobSystem(builder.Configuration);
        builder.Services.AddAgentGateway(builder.Configuration);

        host._app = builder.Build();
        host._app.UseAgentGateway();
        host._app.MapGet("/ping", () => "pong");
        host._app.MapAgentGateway();
        await host._app.StartAsync();

        var addresses = host._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        host.HttpPort = addresses.Select(a => new Uri(a.Replace("0.0.0.0", "127.0.0.1"))).First(u => u.Scheme == "http").Port;
        host.CaPem = (await host.Services.GetRequiredService<IInternalCa>().GetPublicInfoAsync()).CertificatePem;
        return host;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    /// <summary>A pending server and a fresh join token for it.</summary>
    public async Task<(Guid ServerId, string Token)> NewServerWithTokenAsync()
    {
        return await WithDbAsync(async db =>
        {
            var identity = await TestSeeder.SeedIdentityAsync(db);
            var server = new Server { OrganizationId = identity.OrganizationId, Name = "srv-" + Guid.NewGuid().ToString("N")[..10], Host = "203.0.113.20" };
            db.Servers.Add(server);
            var (token, _) = JoinTokenService.Issue(db, server.Id, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return (server.Id, token);
        });
    }

    /// <summary>What an agent does on first contact: pin the CA, enroll over server-authenticated TLS, keep the key.</summary>
    public async Task<EnrolledAgent> EnrollAsync(Guid? serverId = null, string? token = null)
    {
        if (serverId is null)
            (serverId, token) = await NewServerWithTokenAsync();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=whatever", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        using var channel = Channel(null);
        var response = await new EnrollmentService.EnrollmentServiceClient(channel).EnrollAsync(new EnrollRequest
        {
            JoinToken = new Aethera.Agent.V1.SecretValue { Value = token }, AgentVersion = "0.1.0", ProtocolVersion = 1, CsrPem = csr,
            Host = new HostFacts { Hostname = "box", OsName = "Ubuntu", Architecture = "amd64", CpuCoresLogical = 2 },
        });
        return new EnrolledAgent(serverId.Value, key, response, PkiHelpers.WithKey(response.ClientCertificatePem, key));
    }

    /// <summary>A channel the way the agent builds it: TLS 1.3, trusting only the pinned CA, with an optional client certificate.</summary>
    public GrpcChannel Channel(X509Certificate2? clientCertificate, SslProtocols protocols = SslProtocols.Tls13, string? caPem = null) =>
        GrpcChannel.ForAddress($"https://127.0.0.1:{GrpcPort}", new GrpcChannelOptions { HttpHandler = Handler(clientCertificate, protocols, caPem ?? CaPem), DisposeHttpClient = true });

    public SocketsHttpHandler Handler(X509Certificate2? clientCertificate, SslProtocols protocols = SslProtocols.Tls13, string? caPem = null)
    {
        using var ca = X509Certificate2.CreateFromPem(caPem ?? CaPem);
        var pinned = new X509Certificate2(ca);
        var ssl = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = protocols,
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(pinned);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate)) && chain.ChainElements[^1].Certificate.Thumbprint == pinned.Thumbprint;
            },
        };
        if (clientCertificate is not null) ssl.ClientCertificates = new X509CertificateCollection { clientCertificate };
        return new SocketsHttpHandler { SslOptions = ssl };
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        await _database.DisposeAsync();
    }
}

public sealed record EnrolledAgent(Guid ServerId, ECDsa Key, EnrollResponse Response, X509Certificate2 Certificate)
{
    public string Serial => Certificate.SerialNumber;
}

public sealed class GrpcGatewayFixture : IAsyncLifetime
{
    public GrpcGatewayHost? Host { get; private set; }

    public async Task InitializeAsync() => Host = await GrpcGatewayHost.StartAsync();

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class GrpcGatewayCollection : ICollectionFixture<GrpcGatewayFixture>
{
    public const string Name = "grpc-gateway";
}

[Collection(GrpcGatewayCollection.Name)]
public sealed class GrpcGatewayTests(GrpcGatewayFixture fixture)
{
    private GrpcGatewayHost Host => fixture.Host!;

    private static Hello HelloFor(Guid serverId, string version = "0.1.0") =>
        new() { ServerId = serverId.ToString("D"), AgentVersion = version, ProtocolVersion = 1, Os = "linux", Architecture = "amd64", ProcessId = "p1" };

    /// <summary>The call never got past the TLS handshake (not an application-level refusal such as PERMISSION_DENIED).</summary>
    private static void AssertHandshakeFailed(Exception ex)
    {
        var text = ex.ToString();
        Assert.True(ex is RpcException { StatusCode: StatusCode.Internal or StatusCode.Unavailable } && text.Contains("SSL connection could not be established"), text);
    }

    private static async Task<AsyncDuplexStreamingCall<AgentMessage, ControlMessage>> ConnectAsync(GrpcChannel channel, Hello hello)
    {
        var call = new AgentService.AgentServiceClient(channel).Connect();
        await call.RequestStream.WriteAsync(new AgentMessage { Hello = hello });
        return call;
    }

    private static async Task<ControlMessage> ReadAsync(AsyncDuplexStreamingCall<AgentMessage, ControlMessage> call, int timeoutMs = 10000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token), "the server closed the stream");
        return call.ResponseStream.Current;
    }

    [RequiresDatabaseFact]
    public async Task Enrollment_works_over_server_authenticated_tls_without_a_client_certificate()
    {
        var agent = await Host.EnrollAsync();

        Assert.Equal(agent.ServerId.ToString("D"), agent.Response.ServerId);
        Assert.Equal($"localhost:{Host.GrpcPort}", agent.Response.ControlPlaneEndpoint);
        Assert.True(AgentIdentity.TryFromCertificate(agent.Certificate, out var identity));
        Assert.Equal(agent.ServerId, identity.ServerId);
        var info = await Host.Services.GetRequiredService<IInternalCa>().GetPublicInfoAsync();
        Assert.Equal(info.FingerprintSha256, agent.Response.CaFingerprintSha256);
        Assert.Equal(ServerLifecycle.Active, (await Host.WithDbAsync(db => db.Servers.AsNoTracking().SingleAsync(s => s.Id == agent.ServerId))).Lifecycle);
    }

    [RequiresDatabaseFact]
    public async Task The_listener_certificate_carries_the_ca_for_agents_that_only_pinned_its_fingerprint()
    {
        // TLS stacks strip a self-signed root from the chain they send, so an agent that has pinned only the CA fingerprint (enrollment)
        // could not authenticate the server from the handshake alone (found by the Go agent end-to-end test; Grpc.Net clients with the
        // CA in their trust store never noticed). The CA therefore travels inside the listener certificate.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Host.GrpcPort);
        X509Certificate2? presented = null;
        using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            presented = new X509Certificate2(certificate!);
            return true;
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls13, ApplicationProtocols = [SslApplicationProtocol.Http2],
        });

        var embedded = Assert.Single(presented!.Extensions.Cast<X509Extension>(), e => e.Oid?.Value == InternalCa.EmbeddedCaOid);
        using var ca = X509Certificate2.CreateFromPem(Host.CaPem);
        Assert.Equal(ca.RawData, embedded.RawData);
        Assert.False(embedded.Critical);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(embedded.RawData));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(presented), "the listener certificate must verify against the embedded CA");
    }

    [RequiresDatabaseFact]
    public async Task A_replayed_expired_or_unknown_token_is_permission_denied_with_one_message()
    {
        var (serverId, token) = await Host.NewServerWithTokenAsync();
        await Host.EnrollAsync(serverId, token);
        var (_, expired) = await Host.WithDbAsync(async db =>
        {
            var identity = await TestSeeder.SeedIdentityAsync(db);
            var server = new Server { OrganizationId = identity.OrganizationId, Name = "e-" + Guid.NewGuid().ToString("N")[..8], Host = "h" };
            db.Servers.Add(server);
            var (plaintext, _) = JoinTokenService.Issue(db, server.Id, DateTimeOffset.UtcNow.AddHours(-3), TimeSpan.FromMinutes(5));
            await db.SaveChangesAsync();
            return (server.Id, plaintext);
        });

        var messages = new List<string>();
        foreach (var attempt in new[] { token, expired, JoinTokenService.Generate(), "garbage" })
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => Host.EnrollAsync(serverId, attempt));
            Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
            messages.Add(ex.Status.Detail);
        }

        Assert.Single(messages.Distinct());
    }

    [RequiresDatabaseFact]
    public async Task A_malformed_csr_is_an_invalid_argument()
    {
        var (_, token) = await Host.NewServerWithTokenAsync();
        using var channel = Host.Channel(null);
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await new EnrollmentService.EnrollmentServiceClient(channel).EnrollAsync(new EnrollRequest
        {
            JoinToken = new Aethera.Agent.V1.SecretValue { Value = token }, ProtocolVersion = 1, CsrPem = "nonsense",
        }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Enrollment_is_rate_limited_per_source_address()
    {
        await using var limited = await GrpcGatewayHost.StartAsync(enrollPermits: 3);
        var denied = 0;
        var limitedAt = 0;
        for (var i = 1; i <= 6; i++)
        {
            try
            {
                await limited!.EnrollAsync(Guid.NewGuid(), JoinTokenService.Generate());
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                denied++;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
            {
                limitedAt = limitedAt == 0 ? i : limitedAt;
                Assert.NotEmpty(ex.Trailers.GetValue("retry-after") ?? "");
            }
        }

        Assert.Equal(3, denied); // three attempts reached the token check ...
        Assert.Equal(4, limitedAt); // ... and the fourth was stopped before it
    }

    [RequiresDatabaseFact]
    public async Task The_stream_requires_a_client_certificate()
    {
        using var channel = Host.Channel(null);
        using var call = await ConnectAsync(channel, HelloFor(Guid.NewGuid()));
        var ex = await Assert.ThrowsAsync<RpcException>(() => ReadAsync(call));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task An_enrolled_agent_connects_and_the_server_becomes_connected()
    {
        var agent = await Host.EnrollAsync();
        using var channel = Host.Channel(agent.Certificate);
        using var call = await ConnectAsync(channel, HelloFor(agent.ServerId));

        var welcome = (await ReadAsync(call)).Welcome;
        Assert.Equal(agent.ServerId.ToString("D"), welcome.ServerId);
        Assert.Equal(TimeSpan.FromSeconds(0.5), welcome.HeartbeatInterval.ToTimeSpan());

        await call.RequestStream.WriteAsync(new AgentMessage { Heartbeat = new Aethera.Agent.V1.Heartbeat { Seq = 1, SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), DockerStatus = Aethera.Agent.V1.DockerStatus.Running } });
        await call.RequestStream.WriteAsync(new AgentMessage { Metrics = new MetricsReport { Host = new HostMetrics { CpuPercent = 5, MemoryTotalBytes = 100, MemoryUsedBytes = 50 } } });
        await GatewayFixture.EventuallyAsync(async () =>
        {
            var server = await Host.WithDbAsync(db => db.Servers.AsNoTracking().SingleAsync(s => s.Id == agent.ServerId));
            var samples = await Host.WithDbAsync(db => db.MetricSamples.CountAsync(m => m.ServerId == agent.ServerId));
            return server is { AgentStatus: AgentStatus.Connected, DockerStatus: Aethera.Domain.DockerStatus.Running, CertSerial: not null } && samples == 1;
        }, "connected, docker running and a metric sample stored");

        await call.RequestStream.CompleteAsync();
    }

    [RequiresDatabaseFact]
    public async Task A_stream_that_claims_another_servers_identity_is_closed_as_a_protocol_violation()
    {
        var agent = await Host.EnrollAsync();
        using var channel = Host.Channel(agent.Certificate);
        using var call = await ConnectAsync(channel, HelloFor(Guid.NewGuid()));
        var goodbye = (await ReadAsync(call)).Disconnect;
        Assert.Equal(DisconnectReason.ProtocolViolation, goodbye.Reason);
    }

    [RequiresDatabaseFact]
    public async Task A_certificate_of_another_authority_is_refused_at_the_handshake()
    {
        var agent = await Host.EnrollAsync();
        // Same name, same SAN, same shape, different CA.
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Aethera Internal CA", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest($"CN={agent.ServerId:D}", leafKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(AgentIdentity.SubjectUri(agent.ServerId)));
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var forged = leafRequest.Create(root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
        using var forgedWithKey = forged.CopyWithPrivateKey(leafKey);
        var clientCertificate = X509CertificateLoader.LoadPkcs12(forgedWithKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

        using var channel = Host.Channel(clientCertificate);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var call = await ConnectAsync(channel, HelloFor(agent.ServerId));
            await ReadAsync(call);
        });
    }

    [RequiresDatabaseFact]
    public async Task Revoking_a_certificate_closes_the_live_stream_and_blocks_the_next_handshake()
    {
        var agent = await Host.EnrollAsync();
        using var channel = Host.Channel(agent.Certificate);
        using var call = await ConnectAsync(channel, HelloFor(agent.ServerId));
        await ReadAsync(call); // Welcome

        var ca = Host.Services.GetRequiredService<IInternalCa>();
        await Host.WithDbAsync(async db =>
        {
            await ca.RevokeServerCertificatesAsync(db, agent.ServerId, "stolen", DateTimeOffset.UtcNow);
            return await db.SaveChangesAsync();
        });
        Host.Services.GetRequiredService<AgentSessionRegistry>().Disconnect(agent.ServerId, DisconnectReason.Revoked, "revoked");
        Assert.Equal(DisconnectReason.Revoked, (await ReadAsync(call)).Disconnect.Reason);

        using var again = Host.Channel(agent.Certificate);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var second = await ConnectAsync(again, HelloFor(agent.ServerId));
            await ReadAsync(second);
        });
    }

    [RequiresDatabaseFact]
    public async Task A_revocation_known_only_to_the_database_still_stops_a_new_stream()
    {
        // As if another API instance revoked it: this instance's in-memory list does not know yet.
        var agent = await Host.EnrollAsync();
        await Host.WithDbAsync(async db =>
        {
            (await db.AgentCertificates.SingleAsync(c => c.Serial == agent.Serial)).Revoke("other instance", DateTimeOffset.UtcNow);
            return await db.SaveChangesAsync();
        });
        using var channel = Host.Channel(agent.Certificate);
        using var call = await ConnectAsync(channel, HelloFor(agent.ServerId));
        var ex = await Assert.ThrowsAsync<RpcException>(() => ReadAsync(call));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_certificate_is_renewed_over_mtls_and_the_old_one_is_retired_at_the_next_connect()
    {
        var agent = await Host.EnrollAsync();
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var channel = Host.Channel(agent.Certificate);
        var renewed = await new AgentService.AgentServiceClient(channel).RenewCertificateAsync(new RenewCertificateRequest
        {
            CsrPem = new CertificateRequest("CN=x", newKey, HashAlgorithmName.SHA256).CreateSigningRequestPem(),
        });
        using var newCertificate = PkiHelpers.WithKey(renewed.ClientCertificatePem, newKey);
        Assert.NotEqual(agent.Serial, newCertificate.SerialNumber);

        using var reconnect = Host.Channel(newCertificate);
        using var call = await ConnectAsync(reconnect, HelloFor(agent.ServerId));
        Assert.NotNull((await ReadAsync(call)).Welcome);
        await GatewayFixture.EventuallyAsync(async () =>
            (await Host.WithDbAsync(db => db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Serial == agent.Serial))).RevokedAt is not null, "the replaced certificate revoked");

        using var stale = Host.Channel(agent.Certificate);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var rejected = await ConnectAsync(stale, HelloFor(agent.ServerId));
            await ReadAsync(rejected);
        });
    }

    [RequiresDatabaseFact]
    public async Task Renewal_needs_a_client_certificate()
    {
        using var channel = Host.Channel(null);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await new AgentService.AgentServiceClient(channel).RenewCertificateAsync(new RenewCertificateRequest
        {
            CsrPem = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256).CreateSigningRequestPem(),
        }));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Only_tls_13_is_accepted()
    {
        using var channel = Host.Channel(null, SslProtocols.Tls12);
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await new EnrollmentService.EnrollmentServiceClient(channel).EnrollAsync(new EnrollRequest { ProtocolVersion = 1 }));
        AssertHandshakeFailed(ex);
    }

    [RequiresDatabaseFact]
    public async Task An_agent_that_does_not_trust_the_ca_cannot_even_start_a_call()
    {
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherRoot = new CertificateRequest("CN=Aethera Internal CA", otherKey, HashAlgorithmName.SHA256);
        otherRoot.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        using var other = otherRoot.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var channel = Host.Channel(null, caPem: other.ExportCertificatePem()); // the pin does not match the control plane's certificate
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await new EnrollmentService.EnrollmentServiceClient(channel).EnrollAsync(new EnrollRequest { ProtocolVersion = 1 }));
        AssertHandshakeFailed(ex);
    }

    [RequiresDatabaseFact]
    public async Task The_agent_services_exist_only_on_the_gateway_port_and_the_api_only_off_it()
    {
        // The token must never travel over the plain-HTTP API listener: the same call there is "not found" (gRPC UNIMPLEMENTED).
        using var plainHttp = new HttpClient();
        var attempt = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Host.HttpPort}/aethera.agent.v1.EnrollmentService/Enroll")
        {
            Content = new ByteArrayContent([0, 0, 0, 0, 0]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc") } },
        };
        Assert.Equal(HttpStatusCode.NotFound, (await plainHttp.SendAsync(attempt)).StatusCode);

        using var http = new HttpClient(Host.Handler(null)) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"https://localhost:{Host.GrpcPort}/ping")).StatusCode); // an API route is not served on the mTLS port
        using var apiClient = new HttpClient();
        Assert.Equal("pong", await apiClient.GetStringAsync($"http://127.0.0.1:{Host.HttpPort}/ping"));
    }
}
