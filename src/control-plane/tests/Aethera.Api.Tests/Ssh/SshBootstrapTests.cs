using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Bootstrap;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Ssh;

public sealed class FakeSessionProbe : IAgentSessionProbe
{
    public volatile bool Connected;

    /// <summary>The probe reports a session after this many questions (the agent takes a moment to connect).</summary>
    public int ConnectAfter { get; set; } = -1;

    private int _asked;

    public bool IsConnected(Guid serverId) => Connected || (ConnectAfter >= 0 && Interlocked.Increment(ref _asked) > ConnectAfter);
}

/// <summary>The bootstrap flow over a scripted SSH session: every step, its order, the checks that stop it, and the token handling.</summary>
public sealed class SshBootstrapTests : IAsyncLifetime
{
    private const string Staging = "/tmp/aethera-install.AbCd1234";

    private SshTestHost? _host;
    private SshTestHost Host => _host!;
    private readonly FakeSessionProbe _probe = new();
    private string _binaryPath = "";
    private string _binarySha = "";
    private Guid _server;

    public async Task InitializeAsync()
    {
        _binaryPath = Path.Combine(Path.GetTempPath(), "aethera-agent-test-" + Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("#!/bin/sh\necho test agent\n");
        await File.WriteAllBytesAsync(_binaryPath, bytes);
        _binarySha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        _host = await SshTestHost.CreateAsync(
            customize: services => services.AddSingleton<IAgentSessionProbe>(_probe),
            settings: new Dictionary<string, string?>
            {
                ["Aethera:Ssh:Agent:Binaries:linux-amd64:LocalPath"] = _binaryPath,
                ["Aethera:Ssh:Agent:Binaries:linux-amd64:Sha256"] = _binarySha,
                ["Aethera:Ssh:Agent:Binaries:linux-arm64:Url"] = "https://downloads.example.com/aethera-agent-linux-arm64",
                ["Aethera:Ssh:Agent:Binaries:linux-arm64:Sha256"] = new string('a', 64),
            });
        if (_host is not null) _server = await _host.SeedServerAsync("pw-bootstrap", user: "deploy");
    }

    public async Task DisposeAsync()
    {
        File.Delete(_binaryPath);
        if (_host is not null) await _host.DisposeAsync();
    }

    private FakeSshConnection Ssh => Host.Connector!.Connection;

    /// <summary>A healthy non-root Linux host with sudo: answers every step the bootstrap takes.</summary>
    private void HappyHost(string uid = "1000", string arch = "x86_64", Func<RemoteCommand, RemoteResult?>? override_ = null)
    {
        Ssh.Handler = c =>
        {
            if (override_?.Invoke(c) is { } custom) return custom;
            var line = c.Line;
            if (line.StartsWith("sh -c 'id -u")) return new RemoteResult(0, $"{uid}\nLinux\n{arch}\n", "");
            if (line.StartsWith("mktemp")) return new RemoteResult(0, Staging + "\n", "");
            if (line.StartsWith("sha256sum")) return new RemoteResult(0, $"{_binarySha}  {Staging}/aethera-agent\n", "");
            if (line.Contains("install.sh")) return new RemoteResult(0, "creating the aethera-agent user\nenrolling with the control plane\ninstalled: aethera-agent 0.1.0\n", "");
            return new RemoteResult(0, "", "");
        };
    }

    private async Task<(SshInstallResult Result, List<string> Log, SecretRedactor Redactor)> InstallAsync(string? expectedFingerprint = null)
    {
        var redactor = new SecretRedactor();
        var log = new List<string>();
        var result = await Host.Get<SshBootstrapService>().InstallAsync(_server, expectedFingerprint, line => { lock (log) log.Add(redactor.Redact(line)); return Task.CompletedTask; }, redactor, CancellationToken.None);
        return (result, log, redactor);
    }

    private async Task<SshBootstrapException> FailsAsync(string code)
    {
        var ex = await Assert.ThrowsAsync<SshBootstrapException>(() => InstallAsync());
        Assert.Equal(code, ex.Code);
        return ex;
    }

    [RequiresDatabaseFact]
    public async Task A_successful_install_runs_every_step_in_order_and_waits_for_the_session()
    {
        HappyHost();
        _probe.ConnectAfter = 3; // the agent needs a moment to connect
        var (result, log, _) = await InstallAsync();

        Assert.Equal(("amd64", _binarySha, "aethera-agent 0.1.0", true), (result.Architecture, result.BinarySha256, result.InstalledVersion, result.SessionConnected));

        var lines = Ssh.Lines();
        int Index(string start) => Array.FindIndex(lines, l => l.StartsWith(start, StringComparison.Ordinal));
        Assert.True(Index("sh -c 'id -u") < Index("sudo -n true"));
        Assert.True(Index("sudo -n true") < Index("command -v systemctl"));
        Assert.True(Index("command -v systemctl") < Index("mktemp"));
        Assert.True(Index("mktemp") < Index("sha256sum"));
        Assert.True(Index("sha256sum") < Index("sudo -n sh"));
        Assert.True(Index("sudo -n sh") < Index("rm -rf"));
        Assert.Equal($"rm -rf -- {Staging}", lines[^1]); // the staging directory is removed

        // Binary, unit, config and install script are uploaded under the staging directory; the binary is checked on the host before use.
        var uploads = Ssh.Uploads.ToList();
        Assert.Equal([$"{Staging}/aethera-agent", $"{Staging}/aethera-agent.service", $"{Staging}/agent.yaml", $"{Staging}/install.sh"], uploads.Select(u => u.Path));
        Assert.Equal(["0700", "0644", "0644", "0700"], uploads.Select(u => u.Mode));
        Assert.Equal(SshInstallAssets.UnitFile, uploads[1].Content);
        Assert.Equal(SshInstallAssets.AgentYaml, uploads[2].Content);
        Assert.Equal(SshInstallAssets.InstallScript, uploads[3].Content);

        Assert.Contains(log, l => l.Contains("matches the expected SHA-256"));
        Assert.Contains(log, l => l.Contains("agent is connected"));
    }

    [RequiresDatabaseFact]
    public async Task The_join_token_goes_over_stdin_into_a_root_script_and_never_into_argv_or_the_log()
    {
        HappyHost(override_: c => c.Line.Contains("install.sh") ? new RemoteResult(0, "enrolled with " + c.Stdin!.Trim() + "\ninstalled: aethera-agent 0.1.0\n", "") : null);
        _probe.Connected = true;
        var (_, log, _) = await InstallAsync();

        var install = Ssh.Commands.Single(c => c.Line.Contains("install.sh"));
        var token = install.Stdin!.Trim();
        Assert.StartsWith(JoinTokenService.Prefix, token);
        Assert.Equal(token + "\n", install.Stdin);
        Assert.DoesNotContain(token, install.Line);
        Assert.StartsWith($"sudo -n sh {Staging}/install.sh {Staging} aethera.example.com:9443 ", install.Line);
        Assert.Matches(@"[0-9a-f]{64}$", install.Line); // the CA pin the agent verifies the control plane with
        Assert.Contains(token, install.Secrets!);
        Assert.DoesNotContain(log, l => l.Contains(token)); // an echo from the host is masked
        Assert.Contains(log, l => l.Contains("********"));

        // The token is fresh, bound to this server, short-lived and stored only as a hash.
        var rows = await Host.WithDbAsync(db => db.JoinTokens.AsNoTracking().Where(t => t.ServerId == _server).ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal(JoinTokenService.Hash(token), row.TokenHash);
        Assert.InRange((row.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes, 13, 16);
        Assert.Null(row.RevokedAt);
    }

    [RequiresDatabaseFact]
    public async Task A_root_login_runs_the_script_directly_without_sudo()
    {
        HappyHost(uid: "0");
        _probe.Connected = true;
        await InstallAsync();
        Assert.DoesNotContain(Ssh.Lines(), l => l.StartsWith("sudo"));
        Assert.Contains(Ssh.Lines(), l => l.StartsWith($"sh {Staging}/install.sh"));
    }

    [RequiresDatabaseFact]
    public async Task A_binary_that_does_not_match_the_expected_checksum_stops_the_install_before_anything_runs()
    {
        HappyHost(override_: c => c.Line.StartsWith("sha256sum") ? new RemoteResult(0, new string('0', 64) + "  x\n", "") : null);
        await FailsAsync(SshBootstrapCodes.ChecksumMismatch);

        Assert.DoesNotContain(Ssh.Lines(), l => l.Contains("install.sh"));
        Assert.Empty(await Host.WithDbAsync(db => db.JoinTokens.AsNoTracking().Where(t => t.ServerId == _server).ToListAsync())); // no token was even issued
        Assert.Equal($"rm -rf -- {Staging}", Ssh.Lines()[^1]);
    }

    [RequiresDatabaseFact]
    public async Task A_local_binary_that_differs_from_the_configured_sha256_is_never_uploaded()
    {
        await File.WriteAllTextAsync(_binaryPath, "tampered");
        HappyHost();
        await FailsAsync(SshBootstrapCodes.ChecksumMismatch);
        Assert.DoesNotContain(Ssh.Uploads, u => u.Path.EndsWith("aethera-agent", StringComparison.Ordinal));
    }

    [RequiresDatabaseFact]
    public async Task A_download_url_needs_https_and_a_sha256_and_runs_curl_or_wget_on_the_host()
    {
        HappyHost(arch: "aarch64", override_: c =>
            c.Line.StartsWith("sha256sum") ? new RemoteResult(0, new string('a', 64) + "  x\n", "") : null);
        _probe.Connected = true;
        var (result, _, _) = await InstallAsync();

        Assert.Equal("arm64", result.Architecture);
        var download = Ssh.Commands.Single(c => c.Line.Contains("curl -fsSL"));
        Assert.Contains("https://downloads.example.com/aethera-agent-linux-arm64", download.Line);
        Assert.DoesNotContain(Ssh.Uploads, u => u.Path.EndsWith("aethera-agent", StringComparison.Ordinal)); // nothing uploaded from here
    }

    [RequiresDatabaseFact]
    public async Task A_host_the_installer_cannot_serve_is_refused_with_a_reason()
    {
        HappyHost(arch: "armv5l");
        Assert.Contains("armv5l", (await FailsAsync(SshBootstrapCodes.Unsupported)).Message);

        HappyHost(override_: c => c.Line.StartsWith("sh -c 'id -u") ? new RemoteResult(0, "1000\nDarwin\nx86_64\n", "") : null);
        Assert.Contains("Darwin", (await FailsAsync(SshBootstrapCodes.Unsupported)).Message);

        HappyHost(override_: c => c.Line == "sudo -n true" ? new RemoteResult(1, "", "sudo: a password is required") : null);
        await FailsAsync(SshBootstrapCodes.NoPrivileges);

        HappyHost(override_: c => c.Line.StartsWith("command -v systemctl") ? new RemoteResult(1, "", "") : null);
        Assert.Contains("systemd", (await FailsAsync(SshBootstrapCodes.Unsupported)).Message);

        Assert.DoesNotContain(Ssh.Lines(), l => l.Contains("install.sh"));
    }

    [RequiresDatabaseFact]
    public async Task Without_a_configured_binary_for_the_architecture_the_message_names_the_config_key()
    {
        var host = await SshTestHost.CreateAsync(customize: s => s.AddSingleton<IAgentSessionProbe>(new FakeSessionProbe()));
        await using var _ = host;
        var server = await host!.SeedServerAsync("pw", user: "deploy");
        host.Connector!.Connection.Handler = c => c.Line.StartsWith("sh -c 'id -u") ? new RemoteResult(0, "0\nLinux\nx86_64\n", "") : c.Line.StartsWith("mktemp") ? new RemoteResult(0, Staging + "\n", "") : new RemoteResult(0, "", "");

        var ex = await Assert.ThrowsAsync<SshBootstrapException>(() =>
            host.Get<SshBootstrapService>().InstallAsync(server, null, _ => Task.CompletedTask, new SecretRedactor(), CancellationToken.None));
        Assert.Equal(SshBootstrapCodes.NoBinary, ex.Code);
        Assert.Contains("Aethera:Ssh:Agent:Binaries:linux-amd64", ex.Message);
    }

    [RequiresDatabaseFact]
    public async Task When_the_agent_never_connects_the_install_fails_with_the_host_log_and_the_token_is_revoked()
    {
        HappyHost(override_: c => c.Line.StartsWith("sudo -n journalctl") ? new RemoteResult(0, "agent: dial tcp: connection refused\n", "") : null);
        _probe.Connected = false;
        var redactor = new SecretRedactor();
        var log = new List<string>();
        var ex = await Assert.ThrowsAsync<SshBootstrapException>(() =>
            Host.Get<SshBootstrapService>().InstallAsync(_server, null, l => { log.Add(l); return Task.CompletedTask; }, redactor, CancellationToken.None));

        Assert.Equal(SshBootstrapCodes.SessionTimeout, ex.Code);
        Assert.Contains("aethera.example.com:9443", ex.Message);
        Assert.Contains(log, l => l.Contains("connection refused"));
        var row = Assert.Single(await Host.WithDbAsync(db => db.JoinTokens.AsNoTracking().Where(t => t.ServerId == _server).ToListAsync()));
        Assert.NotNull(row.RevokedAt); // an unused token does not outlive a failed install
    }

    [RequiresDatabaseFact]
    public async Task A_failing_install_script_reports_its_error_without_the_token()
    {
        HappyHost(override_: c => c.Line.Contains("install.sh") ? new RemoteResult(1, "", $"useradd: failure while token={c.Stdin!.Trim()}") : null);
        var ex = await FailsAsync(SshBootstrapCodes.Failed);
        Assert.Contains("useradd", ex.Message);
        Assert.DoesNotContain(JoinTokenService.Prefix, ex.Message);
    }

    [RequiresDatabaseFact]
    public async Task A_loopback_control_plane_endpoint_cannot_work_from_another_machine()
    {
        var host = await SshTestHost.CreateAsync(settings: new Dictionary<string, string?> { ["Aethera:Agents:PublicEndpoint"] = "localhost:9443" });
        await using var _ = host;
        var server = await host!.SeedServerAsync();
        var ex = await Assert.ThrowsAsync<SshBootstrapException>(() =>
            host.Get<SshBootstrapService>().InstallAsync(server, null, _ => Task.CompletedTask, new SecretRedactor(), CancellationToken.None));
        Assert.Equal(SshBootstrapCodes.EndpointUnreachable, ex.Code);
        Assert.Equal(0, host.Connector!.Connects); // checked before connecting
    }

    [RequiresDatabaseFact]
    public async Task A_host_key_the_user_verified_is_pinned_first_and_a_different_one_stops_the_install()
    {
        HappyHost();
        _probe.Connected = true;
        var presented = Ssh.HostKey.Fingerprint;
        await InstallAsync(expectedFingerprint: presented);
        Assert.Equal(presented, (await Host.LoadServerAsync(_server)).SshHostKeyFingerprint);

        var other = await Host.SeedServerAsync("pw", user: "deploy", pinned: "SHA256:" + new string('Q', 43));
        var ex = await Assert.ThrowsAsync<SshBootstrapException>(() =>
            Host.Get<SshBootstrapService>().InstallAsync(other, presented, _ => Task.CompletedTask, new SecretRedactor(), CancellationToken.None));
        Assert.Equal(SshBootstrapCodes.Failed, ex.Code);
        Assert.Contains("does not match", ex.Message);
    }

    [RequiresDatabaseFact]
    public async Task A_changed_host_key_blocks_the_install_like_any_other_command()
    {
        var other = await Host.SeedServerAsync("pw", user: "deploy", pinned: "SHA256:" + new string('Q', 43));
        var ex = await Assert.ThrowsAsync<ServerTransportException>(() =>
            Host.Get<SshBootstrapService>().InstallAsync(other, null, _ => Task.CompletedTask, new SecretRedactor(), CancellationToken.None));
        Assert.Equal(SshErrors.HostKeyChanged, ex.Code);
    }

    [RequiresDatabaseFact]
    public async Task The_install_job_is_registered_and_maps_failures_to_job_errors()
    {
        var handler = Host.Services.GetServices<IJobHandler>().OfType<ServerInstallAgentJobHandler>().Single();
        Assert.Equal("server.install_agent", handler.Type);
        Assert.False(handler.Resumable); // a crashed install is started again by the user with a new join token
    }
}

/// <summary>
/// The whole bootstrap against a real sshd with a real shell and the real install script (<c>Ssh/sshd</c>): the stand-in agent records how it
/// was invoked, so the test sees that the token arrived on stdin and nowhere else.
/// </summary>
[Collection("sshd")]
public sealed class SshBootstrapSshdTests : IAsyncLifetime
{
    private const string StubAgent = """
        #!/bin/sh
        case "$1" in
          --version|version) echo "aethera-agent 0.0.0-test"; exit 0 ;;
          enroll)
            shift
            tok=""
            case " $* " in *" --token - "*) read tok ;; esac
            printf '%s\n' "$*" > /var/lib/aethera/enroll-args.txt
            printf 'token-length=%s\nuser=%s\n' "${#tok}" "$(id -un)" > /var/lib/aethera/enroll-info.txt
            [ -n "$tok" ] || exit 2
            echo "enrolled as server test"
            exit 0 ;;
        esac
        exit 1
        """;

    private SshTestHost? _host;
    private string _binaryPath = "";

    public async Task InitializeAsync()
    {
        if (!SshdEndpoint.IsConfigured || !TestDatabase.IsConfigured) return;
        _binaryPath = Path.Combine(Path.GetTempPath(), "aethera-agent-stub-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(_binaryPath, StubAgent.Replace("\r\n", "\n") + "\n");
        _host = await SshTestHost.CreateAsync(fakeConnector: false,
            customize: s => s.AddSingleton<IAgentSessionProbe>(new FakeSessionProbe { Connected = true }),
            settings: new Dictionary<string, string?>
            {
                ["Aethera:Ssh:Agent:Binaries:linux-amd64:LocalPath"] = _binaryPath,
                ["Aethera:Ssh:Agent:Binaries:linux-arm64:LocalPath"] = _binaryPath,
                ["Aethera:Agents:PublicEndpoint"] = "control-plane.example.com:9443",
            });
    }

    public async Task DisposeAsync()
    {
        if (_binaryPath.Length > 0) File.Delete(_binaryPath);
        if (_host is not null) await _host.DisposeAsync();
    }

    [RequiresSshdFact]
    public async Task The_real_install_script_installs_enrols_and_starts_the_agent_for_root_and_for_a_sudo_user()
    {
        if (_host is null || SshdEndpoint.Container is null) return;
        foreach (var user in new[] { "root", "deploy" })
        {
            var server = await _host.SeedServerAsync(SshdEndpoint.Password, SshdEndpoint.Host, SshdEndpoint.Port, user);
            await SshdEndpoint.DockerExecAsync("sh", "-c", "rm -rf /var/lib/aethera /etc/aethera /var/log/systemctl-calls.log");
            var redactor = new SecretRedactor();
            var log = new List<string>();

            var result = await _host.Get<SshBootstrapService>().InstallAsync(server, null, l => { lock (log) log.Add(redactor.Redact(l)); return Task.CompletedTask; }, redactor, CancellationToken.None);
            Assert.True(result.SessionConnected);
            Assert.Equal("aethera-agent 0.0.0-test", result.InstalledVersion);

            var listing = await SshdEndpoint.DockerExecAsync("sh", "-c",
                "stat -c '%U:%a' /var/lib/aethera/bin/aethera-agent; stat -c '%U:%a' /var/lib/aethera; ls /etc/aethera/agent.yaml /etc/systemd/system/aethera-agent.service; cat /var/lib/aethera/enroll-info.txt; cat /var/lib/aethera/enroll-args.txt; cat /var/log/systemctl-calls.log; ls /tmp | grep aethera-install || echo no-staging-left");
            Assert.Contains("aethera-agent:755", listing);
            Assert.Contains("aethera-agent:700", listing);
            Assert.Contains("/etc/aethera/agent.yaml", listing);
            Assert.Contains("/etc/systemd/system/aethera-agent.service", listing);
            Assert.Contains("user=aethera-agent", listing); // enrolled as the service user, not as root
            Assert.Contains("token-length=53", listing); // the token (aeth_join_ + 43 characters) arrived on stdin
            Assert.Contains("--endpoint control-plane.example.com:9443", listing);
            Assert.Contains("--token -", listing);
            Assert.DoesNotContain("aeth_join_", listing); // never in the agent's argv
            Assert.Contains("daemon-reload", listing);
            Assert.Contains("enable aethera-agent", listing);
            Assert.Contains("restart aethera-agent", listing);
            Assert.Contains("no-staging-left", listing);
            Assert.DoesNotContain(log, l => l.Contains("aeth_join_"));
            Assert.Contains(log, l => l.Contains("installed:"));
        }
    }
}
