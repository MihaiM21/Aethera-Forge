using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Ssh;

/// <summary>The connection pool, trust-on-first-use host keys and credential loading, over a scripted SSH client and a real database.</summary>
public sealed class SshPoolTests : IAsyncLifetime
{
    private SshTestHost? _host;
    private SshTestHost Host => _host!;

    public async Task InitializeAsync() => _host = await SshTestHost.CreateAsync(settings: new Dictionary<string, string?> { ["Aethera:Ssh:IdleDisconnectSeconds"] = "0.3" });

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private const string InspectJson = """[{"Id":"abc","Name":"/web","Image":"sha256:1","State":{"Status":"running"},"Config":{"Image":"nginx"}}]""";

    private static RemoteResult Ok(string stdout = "") => new(0, stdout, "");

    private Task<CommandOutcome<DockerContainer>> StartAsync(Guid serverId) =>
        Host.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(serverId, new ContainerStartCommand("web"), CommandOptions.For("k"), CancellationToken.None);

    [RequiresDatabaseFact]
    public async Task The_first_connect_pins_the_host_key_and_one_connection_serves_every_command()
    {
        var serverId = await Host.SeedServerAsync(host: "198.51.100.7", port: 2222, user: "deploy");
        Host.Connector!.Connection.Handler = c => Ok(c.Line.Contains(" inspect ") ? InspectJson : "");

        for (var i = 0; i < 3; i++) Assert.True((await StartAsync(serverId)).Succeeded);

        Assert.Equal(1, Host.Connector.Connects);
        Assert.Equal(Host.Connector.Connection.HostKey.Fingerprint, (await Host.LoadServerAsync(serverId)).SshHostKeyFingerprint);
        var attempt = Assert.Single(Host.Connector.Attempts);
        Assert.Equal(new SshTarget("198.51.100.7", 2222, "deploy"), attempt.Target);
        Assert.Null(attempt.Expected); // trust on first use
        Assert.Equal("pw-hunter2", attempt.Auth.Password); // the stored secret was decrypted for the connector, and nowhere else
        Assert.Equal(["docker container start -- web", "docker container inspect -- web"], Host.Connector.Connection.Lines().Take(2));

        // Later connections must present the pinned key.
        await Host.Get<SshConnectionPool>().EvictAsync(serverId);
        Assert.True((await StartAsync(serverId)).Succeeded);
        Assert.Equal(Host.Connector.Connection.HostKey.Fingerprint, Host.Connector.Attempts[1].Expected);
    }

    [RequiresDatabaseFact]
    public async Task A_private_key_credential_reaches_the_connector_as_a_key()
    {
        const string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nQUJDREVGRw==\n-----END OPENSSH PRIVATE KEY-----\n";
        var serverId = await Host.SeedServerAsync(SshCredentialFormat.Compose(pem, "phrase", null));
        Host.Connector!.Connection.Handler = c => Ok(c.Line.Contains(" inspect ") ? InspectJson : "");
        await StartAsync(serverId);

        var auth = Assert.Single(Host.Connector.Attempts).Auth;
        Assert.Equal(pem, auth.PrivateKey);
        Assert.Equal("phrase", auth.Passphrase);
        Assert.Null(auth.Password);
    }

    [Fact]
    public void Credential_values_parse_as_key_json_or_password()
    {
        Assert.Equal("hunter2", SshCredentialFormat.Parse("hunter2").Password);
        Assert.Equal("{not json", SshCredentialFormat.Parse("{not json").Password);
        var key = SshCredentialFormat.Parse("-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----");
        Assert.EndsWith("\n", key.PrivateKey); // OpenSSH key parsing wants the final newline
        var json = SshCredentialFormat.Parse("""{"privateKey":"-----BEGIN X-----","passphrase":"p","password":"w"}""");
        Assert.Equal(("-----BEGIN X-----", "p", "w"), (json.PrivateKey, json.Passphrase, json.Password));
        Assert.Equal("pw", SshCredentialFormat.Compose(null, null, "pw"));
        Assert.StartsWith("{", SshCredentialFormat.Compose(null, null, "{tricky"));
        Assert.Equal("{tricky", SshCredentialFormat.Parse(SshCredentialFormat.Compose(null, null, "{tricky")).Password);
    }

    [RequiresDatabaseFact]
    public async Task A_changed_host_key_blocks_the_connection_until_a_user_confirms_it()
    {
        var pinned = "SHA256:" + new string('B', 43);
        var serverId = await Host.SeedServerAsync(pinned: pinned);
        var presented = Host.Connector!.Connection.HostKey.Fingerprint;
        var pool = Host.Get<SshConnectionPool>();

        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => StartAsync(serverId));
        Assert.Equal(SshErrors.HostKeyChanged, ex.Code);
        Assert.False(ex.Transient);
        Assert.Equal(1, Host.Connector.Connects);

        // Recorded for the user, and the connection stays blocked without trying again.
        var pending = await Host.Get<SshHostKeyService>().GetPendingAsync(serverId, CancellationToken.None);
        Assert.Equal(presented, pending!.Fingerprint);
        Assert.Equal(pinned, (await Host.LoadServerAsync(serverId)).SshHostKeyFingerprint);
        Assert.False(pool.Peek(serverId).Available);
        Assert.False((await Host.Transport.GetStatusAsync(serverId, CancellationToken.None)).Available);
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<ServerTransportException>(() => StartAsync(serverId));
        Assert.Equal(1, Host.Connector.Connects);

        var audit = await Host.WithDbAsync(db => db.AuditEvents.Where(e => e.ResourceId == serverId && e.Action == "server.ssh_host_key_changed").ToListAsync());
        Assert.Single(audit); // one event, not one per retry

        // A confirmation of anything but the key that was presented is refused; the right one unblocks.
        var wrong = await Host.Get<SshHostKeyService>().ConfirmAsync(serverId, "SHA256:" + new string('C', 43), CancellationToken.None);
        Assert.Equal(HostKeyConfirmation.Mismatch, wrong.Result);
        var right = await Host.Get<SshHostKeyService>().ConfirmAsync(serverId, presented, CancellationToken.None);
        Assert.Equal(HostKeyConfirmation.Pinned, right.Result);
        Assert.Equal(pinned, right.Previous);
        Assert.Equal(presented, (await Host.LoadServerAsync(serverId)).SshHostKeyFingerprint);
        Assert.Null(await Host.Get<SshHostKeyService>().GetPendingAsync(serverId, CancellationToken.None));

        Host.Connector.Connection.Handler = c => Ok(c.Line.Contains(" inspect ") ? InspectJson : "");
        Assert.True((await StartAsync(serverId)).Succeeded);
        Assert.Equal(2, Host.Connector.Connects);
    }

    [RequiresDatabaseFact]
    public async Task Nothing_to_confirm_is_reported_for_a_server_whose_key_did_not_change()
    {
        var serverId = await Host.SeedServerAsync(pinned: "SHA256:" + new string('B', 43));
        var result = await Host.Get<SshHostKeyService>().ConfirmAsync(serverId, "SHA256:" + new string('D', 43), CancellationToken.None);
        Assert.Equal(HostKeyConfirmation.NothingToConfirm, result.Result);
        Assert.Equal("SHA256:" + new string('B', 43), (await Host.LoadServerAsync(serverId)).SshHostKeyFingerprint);

        // A server that was never connected can be pinned explicitly (the key was verified out of band).
        var fresh = await Host.SeedServerAsync();
        var pinned = await Host.Get<SshHostKeyService>().ConfirmAsync(fresh, "SHA256:" + new string('E', 43), CancellationToken.None);
        Assert.Equal(HostKeyConfirmation.Pinned, pinned.Result);
        Assert.Equal("SHA256:" + new string('E', 43), (await Host.LoadServerAsync(fresh)).SshHostKeyFingerprint);
    }

    [RequiresDatabaseFact]
    public async Task Failed_connects_back_off_and_authentication_failures_are_told_apart()
    {
        var serverId = await Host.SeedServerAsync();
        Host.Connector!.Failure = new SshConnectException("The server could not be reached (ConnectionRefused).");

        var first = await Assert.ThrowsAsync<ServerTransportException>(() => StartAsync(serverId));
        Assert.Equal(TransportErrors.Unreachable, first.Code);
        Assert.True(first.Transient);
        await Assert.ThrowsAsync<ServerTransportException>(() => StartAsync(serverId));
        Assert.Equal(1, Host.Connector.Connects); // inside the back-off window the pool does not stack up timeouts
        Assert.False((await Host.Transport.GetStatusAsync(serverId, CancellationToken.None)).Available);

        await Task.Delay(700);
        Host.Connector.Failure = new SshConnectException("The server refused the SSH credentials.", null, authentication: true);
        var refused = await Assert.ThrowsAsync<ServerTransportException>(() => StartAsync(serverId));
        Assert.Equal(SshErrors.AuthFailed, refused.Code);
        Assert.Equal(2, Host.Connector.Connects);

        await Task.Delay(700);
        Host.Connector.Failure = null;
        Host.Connector.Connection.Handler = c => Ok(c.Line.Contains(" inspect ") ? InspectJson : "");
        Assert.True((await StartAsync(serverId)).Succeeded);
        Assert.True((await Host.Transport.GetStatusAsync(serverId, CancellationToken.None)).Available);
    }

    [RequiresDatabaseFact]
    public async Task At_most_the_configured_number_of_commands_run_at_once_on_a_server()
    {
        var serverId = await Host.SeedServerAsync();
        Host.Connector!.Connection.Delay = TimeSpan.FromMilliseconds(120);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 9).Select(i =>
            Host.Transport.ExecuteAsync<ContainerRemoveCommand, Unit>(serverId, new ContainerRemoveCommand("web" + i), CommandOptions.For("rm" + i), CancellationToken.None)));

        Assert.All(outcomes, o => Assert.True(o.Succeeded));
        Assert.Equal(3, Host.Connector.Connection.MaxConcurrent); // Aethera:Ssh:MaxConcurrentChannelsPerServer = 3 in this host
        Assert.Equal(1, Host.Connector.Connects);
    }

    [RequiresDatabaseFact]
    public async Task Rotating_the_credential_replaces_the_connection()
    {
        var serverId = await Host.SeedServerAsync("old-password");
        Host.Connector!.Connection.Handler = c => Ok(c.Line.Contains(" inspect ") ? InspectJson : "");
        await StartAsync(serverId);

        await Host.WithDbAsync(async db =>
        {
            var server = await db.Servers.AsNoTracking().FirstAsync(s => s.Id == serverId);
            var secret = await db.Secrets.FirstAsync(s => s.Id == server.SshCredentialSecretId);
            new Aethera.Api.Features.Resources.Secrets.SecretVault(db, Host.Get<ISecretProtector>(), Host.Get<IClock>()).AddVersion(secret, "new-password");
            await db.SaveChangesAsync();
            return 0;
        });

        await StartAsync(serverId);
        Assert.Equal(2, Host.Connector.Connects);
        Assert.Equal("new-password", Host.Connector.Attempts[1].Auth.Password);
    }

    [RequiresDatabaseFact]
    public async Task Idle_connections_are_closed_and_reopened_on_demand()
    {
        var serverId = await Host.SeedServerAsync();
        Host.Connector!.Connection.Handler = c => Ok(c.Line.Contains(" inspect ") ? InspectJson : "");
        await StartAsync(serverId);
        var pool = Host.Get<SshConnectionPool>();
        Assert.Equal(1, pool.OpenConnections);

        await Task.Delay(500);
        await pool.CloseIdleAsync();
        Assert.Equal(0, pool.OpenConnections);

        await StartAsync(serverId);
        Assert.Equal(2, Host.Connector.Connects);
    }

    [RequiresDatabaseFact]
    public async Task A_server_without_an_ssh_credential_cannot_use_the_transport()
    {
        var serverId = await Host.SeedServerAsync(withCredential: false);
        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => StartAsync(serverId));
        Assert.Equal(SshErrors.NoCredential, ex.Code);
        Assert.Equal(0, Host.Connector!.Connects);
    }

    [RequiresDatabaseFact]
    public async Task A_dead_connection_is_replaced_on_the_next_command()
    {
        var serverId = await Host.SeedServerAsync();
        Host.Connector!.Connection.Handler = c => Ok(c.Line.Contains(" inspect ") ? InspectJson : "");
        await StartAsync(serverId);

        Host.Connector.Connection.IsConnected = false; // the keep-alive noticed a dead session
        Assert.True((await StartAsync(serverId)).Succeeded);
        Assert.Equal(2, Host.Connector.Connects);
    }
}
