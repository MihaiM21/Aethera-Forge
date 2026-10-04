using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Tests.Ssh;

/// <summary>Credentials and command secrets never reach logs, exceptions, audit events, settings or the database in clear (ADR 0002 "Secret-handling rules for code").</summary>
public sealed class SshCredentialLeakTests : IAsyncLifetime
{
    private const string Password = "Pw-Leak-Check-4471";
    private const string KeyBody = "LEAKKEYBODYQUJDREVGR0hJSktMTU5PUA";
    private const string Passphrase = "Passphrase-Leak-8812";
    private const string EnvSecret = "Env-Leak-Check-9930";

    private SshTestHost? _host;
    private SshTestHost Host => _host!;

    public async Task InitializeAsync() => _host = await SshTestHost.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private static readonly string[] Secrets = [Password, KeyBody, Passphrase, EnvSecret];

    private static string Pem => $"-----BEGIN OPENSSH PRIVATE KEY-----\n{KeyBody}\n-----END OPENSSH PRIVATE KEY-----\n";

    private void AssertNoSecretIn(string text, string where)
    {
        foreach (var secret in Secrets) Assert.False(text.Contains(secret, StringComparison.Ordinal), $"'{secret}' leaked into {where}");
    }

    [Fact]
    public void Credential_objects_print_as_redacted()
    {
        var auth = new SshAuth { Password = Password, PrivateKey = Pem, Passphrase = Passphrase };
        AssertNoSecretIn(auth.ToString(), "SshAuth.ToString");
        AssertNoSecretIn($"{auth}", "interpolation");
        AssertNoSecretIn((auth with { Password = "x" }).ToString(), "a copy");

        var access = new SshServerAccess(Guid.NewGuid(), Guid.NewGuid(), "srv", new SshTarget("h", 22, "root"), auth, null, Guid.NewGuid(), 1);
        AssertNoSecretIn(access.ToString(), "SshServerAccess.ToString");
        AssertNoSecretIn(access.ConnectionKey, "the pool key"); // the key identifies the credential version, not its value

        var secret = new SecretValue(EnvSecret);
        AssertNoSecretIn(secret.ToString(), "SecretValue");
        AssertNoSecretIn(new EnvVarSpec("X", null, secret).ToString(), "EnvVarSpec");
        AssertNoSecretIn(new ContainerCreateCommand(new ContainerSpec("nginx", "web") { Env = [EnvVarSpec.OfSecret("X", EnvSecret)] }).ToString(), "a command");
    }

    [RequiresDatabaseFact]
    public async Task The_stored_credential_is_encrypted_and_only_the_access_provider_reads_it_back()
    {
        var id = await Host.SeedServerAsync(SshCredentialFormat.Compose(Pem, Passphrase, null));
        var raw = await Host.WithDbAsync(async db =>
        {
            var server = await db.Servers.AsNoTracking().FirstAsync(s => s.Id == id);
            return await db.SecretVersions.AsNoTracking().Where(v => v.SecretId == server.SshCredentialSecretId).ToListAsync();
        });
        var row = Assert.Single(raw);
        foreach (var secret in new[] { KeyBody, Passphrase })
        {
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(row.Ciphertext));
            Assert.DoesNotContain(secret, Convert.ToBase64String(row.Ciphertext));
        }

        var access = await Host.Get<SshAccessProvider>().GetAsync(id, CancellationToken.None);
        Assert.Contains(KeyBody, access!.Auth.PrivateKey);
        Assert.Equal(Passphrase, access.Auth.Passphrase);
    }

    [RequiresDatabaseFact]
    public async Task Failing_connections_log_and_throw_without_the_credential()
    {
        var id = await Host.SeedServerAsync(SshCredentialFormat.Compose(Pem, Passphrase, Password));
        var connector = Host.Connector!;
        var pool = Host.Get<SshConnectionPool>();
        var messages = new List<string>();

        async Task Try(Exception failure)
        {
            connector.Failure = failure;
            await pool.EvictAsync(id);
            try { using var _ = await pool.AcquireAsync(id, CancellationToken.None); }
            catch (Exception ex) { messages.Add(ex.ToString()); }
        }

        await Try(new SshConnectException("The server refused the SSH credentials.", new InvalidOperationException("inner"), authentication: true));
        await Try(new SshConnectException("The server could not be reached (ConnectionRefused)."));
        await Try(new SshHostKeyChangedException("SHA256:" + new string('A', 43), new HostKeyInfo("ssh-ed25519", "SHA256:" + new string('B', 43))));
        Assert.Equal(3, messages.Count);
        AssertNoSecretIn(string.Join('\n', messages), "an exception");
        AssertNoSecretIn(string.Join('\n', Host.Logs.Messages), "the logs");
        Assert.NotEmpty(Host.Logs.Messages); // the failures were logged, just without secrets
    }

    [RequiresDatabaseFact]
    public async Task Commands_with_secrets_leave_no_trace_in_logs_audit_settings_or_outcomes()
    {
        var id = await Host.SeedServerAsync(Password);
        var connection = Host.Connector!.Connection;
        connection.Handler = c => c.Line.Contains("container create")
            ? new RemoteResult(1, "", $"Error: invalid env DB_PASSWORD={EnvSecret} for {Password}")
            : new RemoteResult(0, "", "");
        var spec = new ContainerSpec("nginx:1.27", "web") { Env = [EnvVarSpec.OfSecret("DB_PASSWORD", EnvSecret)] };
        var auth = new RegistryCredentials("ghcr.io", "octocat", new SecretValue(EnvSecret));

        var outcome = await Host.Transport.ExecuteAsync<ContainerCreateCommand, ContainerCreated>(
            id, new ContainerCreateCommand(spec, PullAuth: auth, PullIfMissing: true), CommandOptions.For("job:step:0"), CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, outcome.Status);
        AssertNoSecretIn(outcome.ErrorMessage ?? "", "the outcome");
        foreach (var command in connection.Commands) AssertNoSecretIn(command.Line, "a command line");
        AssertNoSecretIn(string.Join('\n', Host.Logs.Messages), "the logs");

        var audit = await Host.WithDbAsync(db => db.AuditEvents.AsNoTracking().Select(e => e.MetadataJson).ToListAsync());
        AssertNoSecretIn(string.Join('\n', audit), "the audit trail");
        var settings = await Host.WithDbAsync(db => db.Settings.AsNoTracking().Select(s => s.ValueJson).ToListAsync());
        AssertNoSecretIn(string.Join('\n', settings), "the settings");
    }

    [RequiresDatabaseFact]
    public async Task An_undecryptable_credential_says_so_without_detail()
    {
        var id = await Host.SeedServerAsync(Password);
        await Host.WithDbAsync(async db =>
        {
            var version = await db.SecretVersions.FirstAsync();
            // The associated data no longer matches (as if the ciphertext was moved to another secret).
            await db.Database.ExecuteSqlRawAsync("UPDATE secret_versions SET nonce = decode('000000000000000000000000','hex') WHERE secret_id = {0}", version.SecretId);
            return 0;
        });
        Host.Get<SshAccessProvider>().Invalidate(id);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Host.Get<SshAccessProvider>().GetAsync(id, CancellationToken.None));
        Assert.IsType<SshConnectException>(ex);
        AssertNoSecretIn(ex.ToString(), "the decrypt failure");

        var transport = await Assert.ThrowsAsync<ServerTransportException>(() => Host.Get<SshConnectionPool>().AcquireAsync(id, CancellationToken.None));
        Assert.Equal(SshErrors.AuthFailed, transport.Code);
        AssertNoSecretIn(transport.ToString(), "the pool failure");
    }
}
