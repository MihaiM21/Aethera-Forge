using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;

namespace Aethera.Api.Tests.Ssh;

/// <summary>
/// The real SSH.NET client against a real sshd (<c>Ssh/sshd</c>, a Debian container): connect, exec, stdin, upload, host key pinning,
/// key and password authentication, port forwarding, streaming, and the quoting of every hostile string through a real shell. Skipped
/// unless <c>AETHERA_TEST_SSH</c> points at the container.
/// </summary>
[Collection("sshd")]
public sealed class SshdIntegrationTests
{
    private static readonly SshNetConnector Connector = new();

    private static Task<ISshConnection> ConnectAsync(string user = "root", SshAuth? auth = null, string? expected = null) =>
        Connector.ConnectAsync(SshdEndpoint.Target(user), auth ?? new SshAuth { Password = SshdEndpoint.Password }, expected, SshdEndpoint.Settings, CancellationToken.None);

    private static Task<RemoteResult> RunAsync(ISshConnection connection, string line, string? stdin = null) =>
        connection.ExecuteAsync(new RemoteCommand(line, stdin), new RemoteExecOptions { Timeout = TimeSpan.FromSeconds(30) }, CancellationToken.None);

    [RequiresSshdFact]
    public async Task Commands_run_on_their_own_channels_with_exit_status_stdout_and_stderr()
    {
        await using var connection = await ConnectAsync();
        var ok = await RunAsync(connection, "sh -c 'echo out; echo err >&2; exit 3'");
        Assert.Equal(3, ok.ExitStatus);
        Assert.Equal("out\n", ok.Stdout);
        Assert.Equal("err\n", ok.Stderr);
        Assert.StartsWith("SHA256:", connection.HostKey.Fingerprint);
        Assert.Equal(50, connection.HostKey.Fingerprint.Length); // "SHA256:" + 43 base64 characters, as ssh-keygen -l prints
    }

    [RequiresSshdFact]
    public async Task Standard_input_reaches_the_command_and_never_the_command_line()
    {
        await using var connection = await ConnectAsync();
        var result = await RunAsync(connection, "cat", "line one\nline two with 'quotes' and $dollars\n");
        Assert.Equal("line one\nline two with 'quotes' and $dollars\n", result.Stdout);
    }

    [RequiresSshdFact]
    public async Task Many_commands_share_one_connection_in_parallel()
    {
        await using var connection = await ConnectAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => RunAsync(connection, $"sh -c 'sleep 0.2; echo {i}'")));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i + "\n"), results.Select(r => r.Stdout));
    }

    [RequiresSshdFact]
    public async Task Every_hostile_string_survives_the_real_ssh_exec_and_shell_as_one_argument()
    {
        await using var connection = await ConnectAsync();
        // The docker stand-in records its argv as JSON: whatever the shell made of our quoting is visible exactly.
        await RunAsync(connection, "rm -f /tmp/docker-calls.jsonl");
        var values = PosixShell.HostileStrings().Concat(PosixShell.RandomHostileStrings(150, seed: 7)).Where(v => !v.Contains('\0')).Distinct().ToList();
        foreach (var value in values)
        {
            var result = await RunAsync(connection, new Cmd("docker").Lit("probe").Arg(value).Lit("--").Arg(value).ToString());
            Assert.True(result.Succeeded, result.Stderr);
        }

        var calls = (await RunAsync(connection, "cat /tmp/docker-calls.jsonl")).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(values.Count, calls.Length);
        for (var i = 0; i < values.Count; i++)
        {
            var argv = JsonDocument.Parse(calls[i]).RootElement.GetProperty("argv").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(["probe", values[i], "--", values[i]], argv);
        }
    }

    [RequiresSshdFact]
    public async Task Uploads_land_atomically_with_the_requested_mode_and_arrive_intact()
    {
        await using var connection = await ConnectAsync();
        var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var path = "/tmp/up-" + Guid.NewGuid().ToString("N");
        await connection.UploadAsync(path, new MemoryStream(bytes), "0600", CancellationToken.None);

        var stat = (await RunAsync(connection, $"stat -c '%a %s' {path}")).Stdout.Trim();
        Assert.Equal($"600 {bytes.Length}", stat);
        var sha = (await RunAsync(connection, $"sha256sum {path}")).Stdout.Split(' ')[0];
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha);
        Assert.Equal("", (await RunAsync(connection, $"ls {path}.tmp.* 2>/dev/null; true")).Stdout.Trim()); // no half-written leftovers

        // A path with spaces and quotes is data, not syntax.
        var odd = "/tmp/odd name 'x' " + Guid.NewGuid().ToString("N");
        await connection.UploadAsync(odd, new MemoryStream(Encoding.UTF8.GetBytes("hello")), "0644", CancellationToken.None);
        Assert.Equal("hello\n", (await RunAsync(connection, "cat " + ShellQuote.Quote(odd))).Stdout); // output is line oriented
        await Assert.ThrowsAsync<ArgumentException>(() => connection.UploadAsync(path, new MemoryStream(), "0600; id", CancellationToken.None));
    }

    [RequiresSshdFact]
    public async Task The_host_key_is_pinned_on_first_contact_and_a_different_key_is_refused()
    {
        await using var first = await ConnectAsync();
        var pinned = first.HostKey.Fingerprint;

        await using var again = await ConnectAsync(expected: pinned); // the same key is accepted
        Assert.Equal(pinned, again.HostKey.Fingerprint);

        var wrong = "SHA256:" + new string('A', 43);
        var changed = await Assert.ThrowsAsync<SshHostKeyChangedException>(() => ConnectAsync(expected: wrong));
        Assert.Equal(wrong, changed.PinnedFingerprint);
        Assert.Equal(pinned, changed.Presented.Fingerprint);
        Assert.DoesNotContain(SshdEndpoint.Password, changed.Message);

        var scanned = await Connector.ScanHostKeyAsync(SshdEndpoint.Host, SshdEndpoint.Port, SshdEndpoint.Settings, CancellationToken.None);
        Assert.Equal(pinned, scanned.Fingerprint);
    }

    [RequiresSshdFact]
    public async Task Authentication_failures_are_told_apart_from_unreachable_hosts_and_leak_no_credentials()
    {
        const string wrongPassword = "definitely-not-the-password-9f3a";
        var refused = await Assert.ThrowsAsync<SshConnectException>(() => ConnectAsync(auth: new SshAuth { Password = wrongPassword }));
        Assert.True(refused.Authentication);
        Assert.DoesNotContain(wrongPassword, refused.ToString());

        var closed = await Assert.ThrowsAsync<SshConnectException>(() =>
            Connector.ConnectAsync(new SshTarget("127.0.0.1", 1, "root"), new SshAuth { Password = wrongPassword }, null, new SshConnectionSettings(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)), CancellationToken.None));
        Assert.False(closed.Authentication);
        Assert.DoesNotContain(wrongPassword, closed.ToString());

        var badKey = await Assert.ThrowsAsync<SshConnectException>(() => ConnectAsync(auth: new SshAuth { PrivateKey = "-----BEGIN RSA PRIVATE KEY-----\nbm90IGEga2V5\n-----END RSA PRIVATE KEY-----\n" }));
        Assert.True(badKey.Authentication);
        Assert.DoesNotContain("bm90IGEga2V5", badKey.ToString());
    }

    [RequiresSshdFact]
    public async Task A_private_key_authenticates_and_a_different_key_does_not()
    {
        if (SshdEndpoint.Container is null) return; // installing the public key needs docker exec into the container
        var (pem, authorizedKey) = TestKeys.NewRsa();
        await SshdEndpoint.DockerExecAsync("sh", "-c", $"mkdir -p /root/.ssh && chmod 700 /root/.ssh && echo '{authorizedKey}' >> /root/.ssh/authorized_keys && chmod 600 /root/.ssh/authorized_keys");

        await using (var connection = await ConnectAsync(auth: new SshAuth { PrivateKey = pem }))
            Assert.Equal("root\n", (await RunAsync(connection, "whoami")).Stdout);

        var (otherPem, _) = TestKeys.NewRsa();
        var denied = await Assert.ThrowsAsync<SshConnectException>(() => ConnectAsync(auth: new SshAuth { PrivateKey = otherPem }));
        Assert.True(denied.Authentication);
        Assert.DoesNotContain("BEGIN", denied.ToString());
    }

    [RequiresSshdFact]
    public async Task A_direct_tcpip_tunnel_reaches_services_on_the_servers_loopback()
    {
        await using var connection = await ConnectAsync();
        // Listens on 127.0.0.1 inside the container only: unreachable from here except through the tunnel.
        await RunAsync(connection, "sh -c 'cd /tmp && nohup python3 -m http.server 18080 --bind 127.0.0.1 >/dev/null 2>&1 &'");
        await Task.Delay(800);

        var prober = new SshHealthProber();
        Task<RemoteResult> Run(RemoteCommand c, CancellationToken t) => connection.ExecuteAsync(c, new RemoteExecOptions(), t);

        var http = await prober.ProbeAsync(connection, new HealthProbeCommand(new HttpProbeTarget("http://127.0.0.1:18080/")), Run, CancellationToken.None);
        Assert.True(http.Healthy, http.Detail);
        Assert.Equal(200, http.HttpStatus);

        var missing = await prober.ProbeAsync(connection, new HealthProbeCommand(new HttpProbeTarget("http://localhost:18080/nope")), Run, CancellationToken.None);
        Assert.False(missing.Healthy);
        Assert.Equal(404, missing.HttpStatus);

        var open = await prober.ProbeAsync(connection, new HealthProbeCommand(new TcpProbeTarget("127.0.0.1", 18080)), Run, CancellationToken.None);
        Assert.True(open.Healthy, open.Detail);
        var closed = await prober.ProbeAsync(connection, new HealthProbeCommand(new TcpProbeTarget("127.0.0.1", 18999)), Run, CancellationToken.None);
        Assert.False(closed.Healthy, closed.Detail);

        var container = await prober.ProbeAsync(connection, new HealthProbeCommand(new ContainerHealthProbeTarget("web")), Run, CancellationToken.None);
        Assert.True(container.Healthy, container.Detail); // the docker stand-in answers "healthy"

        await RunAsync(connection, "pkill -f 'http.server 18080'; true");
    }

    [RequiresSshdFact]
    public async Task Output_streams_while_the_command_runs_and_cancelling_stops_it()
    {
        await using var connection = await ConnectAsync();
        var lines = new List<string>();
        await foreach (var line in connection.StreamAsync(new RemoteCommand("sh -c 'for i in 1 2 3; do echo line$i; echo oops$i >&2; sleep 0.1; done'"), CancellationToken.None))
            lines.Add((line.IsStderr ? "E:" : "O:") + line.Text);
        Assert.Equal(["O:line1", "O:line2", "O:line3"], lines.Where(l => l.StartsWith("O:")));
        Assert.Equal(["E:oops1", "E:oops2", "E:oops3"], lines.Where(l => l.StartsWith("E:")));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var seen = 0;
        try
        {
            await foreach (var _ in connection.StreamAsync(new RemoteCommand("sh -c 'while true; do echo tick; sleep 0.1; done'"), cts.Token)) seen++;
        }
        catch (OperationCanceledException)
        {
            // expected: the consumer stopped
        }

        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
        Assert.True(seen > 0);
        Assert.True(connection.IsConnected);
        Assert.Equal("alive\n", (await RunAsync(connection, "echo alive")).Stdout); // the session survived the cancelled channel
    }

    [RequiresSshdFact]
    public async Task A_command_that_exceeds_its_timeout_is_stopped()
    {
        await using var connection = await ConnectAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => connection.ExecuteAsync(new RemoteCommand("sleep 30"), new RemoteExecOptions { Timeout = TimeSpan.FromMilliseconds(500) }, CancellationToken.None));
        Assert.Equal("ok\n", (await RunAsync(connection, "echo ok")).Stdout);
    }
}
