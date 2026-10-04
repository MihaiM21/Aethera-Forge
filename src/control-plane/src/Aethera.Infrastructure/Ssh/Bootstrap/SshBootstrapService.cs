using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Ssh.Bootstrap;

/// <summary>Tells the bootstrap whether the freshly installed agent's session appeared (replaced in tests).</summary>
public interface IAgentSessionProbe
{
    bool IsConnected(Guid serverId);
}

public sealed class RegistryAgentSessionProbe(AgentSessionRegistry registry) : IAgentSessionProbe
{
    public bool IsConnected(Guid serverId) => registry.IsConnected(serverId);
}

/// <summary>A bootstrap step failed. <see cref="Code"/> is the stable job failure code.</summary>
public sealed class SshBootstrapException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class SshBootstrapCodes
{
    public const string NoBinary = "server.install_no_binary";
    public const string ChecksumMismatch = "server.install_checksum_mismatch";
    public const string Unsupported = "server.install_unsupported_host";
    public const string NoPrivileges = "server.install_no_privileges";
    public const string EndpointUnreachable = "server.install_endpoint_unreachable";
    public const string Failed = "server.install_failed";
    public const string SessionTimeout = "server.install_session_timeout";
}

/// <summary>Result of a successful installation.</summary>
public sealed record SshInstallResult(string Architecture, string BinarySha256, string? InstalledVersion, bool SessionConnected);

/// <summary>
/// "Add server by SSH": installs the agent over an SSH session (ADR 0002 "SshTransport" role 1). Verifies the host key (the pool's TOFU
/// check, or a fingerprint the user supplied), gets the binary onto the host and verifies its SHA-256, writes the systemd unit and
/// <c>agent.yaml</c>, runs <c>aethera-agent enroll</c> with a fresh join token passed on standard input, starts the service and waits for
/// the agent's session to appear in the session registry.
/// </summary>
public sealed partial class SshBootstrapService(
    SshConnectionPool pool, SshHostKeyService hostKeys, IServiceScopeFactory scopes, IInternalCa ca, IAgentSessionProbe sessions, IOptions<SshOptions> options,
    IOptions<AgentGatewayOptions> gateway, IClock clock, TimeProvider time)
{
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9.-]*:[0-9]{1,5}\z")]
    private static partial Regex EndpointPattern();

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z")]
    private static partial Regex Sha256Pattern();

    /// <summary>Installs the agent on <paramref name="serverId"/>, reporting progress lines to <paramref name="log"/>.</summary>
    /// <exception cref="SshBootstrapException">A step failed (checksum, unsupported host, no privileges, session never appeared).</exception>
    /// <exception cref="ServerTransportException">The SSH connection could not be used (unreachable, host key changed, credentials refused).</exception>
    public async Task<SshInstallResult> InstallAsync(Guid serverId, string? expectedHostKeyFingerprint, Func<string, Task> log, ISecretRedactor redactor, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var endpoint = gateway.Value.EffectivePublicEndpoint;
        if (!EndpointPattern().IsMatch(endpoint) || IsLoopback(endpoint))
            throw new SshBootstrapException(SshBootstrapCodes.EndpointUnreachable,
                "Aethera:Agents:PublicEndpoint must be a host:port that the server can reach (it is not loopback). Set it to the control plane's public name or address.");
        var caInfo = await ca.GetPublicInfoAsync(cancellationToken);
        if (!Sha256Pattern().IsMatch(caInfo.FingerprintSha256)) throw new SshBootstrapException(SshBootstrapCodes.Failed, "The CA fingerprint is not available.");

        if (expectedHostKeyFingerprint is not null)
        {
            // A fingerprint the user verified out of band becomes the pin before anything is sent.
            var (result, _) = await hostKeys.ConfirmAsync(serverId, expectedHostKeyFingerprint, cancellationToken);
            if (result == HostKeyConfirmation.Mismatch || result == HostKeyConfirmation.NothingToConfirm)
                throw new SshBootstrapException(SshBootstrapCodes.Failed, "The expected host key fingerprint does not match the one pinned for this server.");
            await pool.EvictAsync(serverId);
        }

        await log("Connecting over SSH");
        using var lease = await pool.AcquireAsync(serverId, cancellationToken);
        var connection = lease.Connection;
        foreach (var secret in lease.Access.Auth.SecretValues()) redactor.Register(secret);
        await log($"Host key {connection.HostKey.Algorithm} {connection.HostKey.Fingerprint} is pinned for this server");

        async Task<RemoteResult> Run(RemoteCommand command, bool stream = false, TimeSpan? timeout = null)
        {
            return await connection.ExecuteAsync(command, new RemoteExecOptions
            {
                Timeout = timeout ?? TimeSpan.FromMinutes(2),
                OnLine = stream ? line => { _ = log(redactor.Redact(line.Text)); } : null,
            }, cancellationToken);
        }

        // ---- 1. look at the host
        var identity = await Run(new RemoteCommand("sh -c 'id -u; uname -s; uname -m'"));
        var parts = identity.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!identity.Succeeded || parts.Length < 3) throw new SshBootstrapException(SshBootstrapCodes.Failed, "The host did not answer the basic system probe (is the login shell POSIX compatible?).");
        var isRoot = parts[0] == "0";
        if (parts[1] != "Linux") throw new SshBootstrapException(SshBootstrapCodes.Unsupported, $"Only Linux hosts are supported (found {parts[1]}).");
        var arch = parts[2] switch { "x86_64" or "amd64" => "amd64", "aarch64" or "arm64" => "arm64", _ => throw new SshBootstrapException(SshBootstrapCodes.Unsupported, $"The CPU architecture {parts[2]} is not supported.") };
        await log($"Host: Linux/{arch}, running as {(isRoot ? "root" : "an unprivileged user")}");
        var privileged = isRoot ? "" : "sudo -n ";
        if (!isRoot && !(await Run(new RemoteCommand("sudo -n true"))).Succeeded)
            throw new SshBootstrapException(SshBootstrapCodes.NoPrivileges, "The SSH user is not root and cannot use passwordless sudo, so the agent cannot be installed.");
        if (!(await Run(new RemoteCommand("command -v systemctl"))).Succeeded)
            throw new SshBootstrapException(SshBootstrapCodes.Unsupported, "systemd (systemctl) was not found on the host; the agent is installed as a systemd service.");

        // ---- 2. staging directory, binary, checksum
        var staging = (await Run(new RemoteCommand("mktemp -d /tmp/aethera-install.XXXXXXXX"))).Stdout.Trim();
        if (!Regex.IsMatch(staging, @"\A/tmp/aethera-install\.[A-Za-z0-9]{8}\z")) throw new SshBootstrapException(SshBootstrapCodes.Failed, "Could not create a staging directory on the host.");
        string? token = null;
        var sessionAppeared = false;
        try
        {
            var sha256 = await PlaceBinaryAsync(connection, settings.Agent, arch, staging, Run, log, cancellationToken);

            await connection.UploadAsync(staging + "/aethera-agent.service", Utf8(SshInstallAssets.UnitFile), "0644", cancellationToken);
            await connection.UploadAsync(staging + "/agent.yaml", Utf8(SshInstallAssets.AgentYaml), "0644", cancellationToken);
            await connection.UploadAsync(staging + "/install.sh", Utf8(SshInstallAssets.InstallScript), "0700", cancellationToken);
            await log("Uploaded the systemd unit, agent.yaml and the install script");

            // ---- 3. enrol and start
            token = await IssueTokenAsync(serverId, cancellationToken);
            redactor.Register(token);
            await log("Issued a one-time join token (it is passed on standard input and expires in " + settings.BootstrapJoinTokenMinutes + " minutes)");
            var install = new RemoteCommand(
                privileged + "sh " + ShellQuote.Quote(staging + "/install.sh") + " " + ShellQuote.Quote(staging) + " " + ShellQuote.Quote(endpoint) + " " + ShellQuote.Quote(caInfo.FingerprintSha256),
                token + "\n", [token]);
            var run = await Run(install, stream: true, TimeSpan.FromMinutes(5));
            if (!run.Succeeded) throw new SshBootstrapException(SshBootstrapCodes.Failed, "The install script failed: " + DockerErrors.Summarize(redactor.Redact(run.Stderr), redactor.Redact(run.Stdout)));
            var version = run.Stdout.Split('\n').LastOrDefault(l => l.StartsWith("installed:", StringComparison.Ordinal))?["installed:".Length..].Trim();

            // ---- 4. wait for the agent's session
            await log("Waiting for the agent to connect");
            var until = time.GetUtcNow() + TimeSpan.FromSeconds(settings.BootstrapSessionWaitSeconds);
            while (time.GetUtcNow() < until)
            {
                if (sessions.IsConnected(serverId)) { sessionAppeared = true; break; }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

            if (!sessionAppeared)
            {
                var journal = await Run(new RemoteCommand(privileged + "journalctl -u aethera-agent -n 15 --no-pager"));
                if (journal.Succeeded) await log("Agent log:\n" + redactor.Redact(journal.Stdout));
                throw new SshBootstrapException(SshBootstrapCodes.SessionTimeout,
                    "The agent was installed and started but did not connect. Check that the server can reach " + endpoint + " and that the CA pin matches.");
            }

            await log("The agent is connected. Aethera now uses it instead of SSH.");
            return new SshInstallResult(arch, sha256, version, true);
        }
        finally
        {
            try { await Run(new Cmd("rm").Lit("-rf").Lit("--").Arg(staging).ToRemote()); } catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort */ }
            if (!sessionAppeared && token is not null) await RevokeUnusedTokenAsync(serverId, token);
        }
    }

    private static bool IsLoopback(string endpoint)
    {
        var host = endpoint[..endpoint.LastIndexOf(':')];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.", StringComparison.Ordinal) || host is "0.0.0.0" or "::1";
    }

    private static Stream Utf8(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private async Task<string> PlaceBinaryAsync(
        ISshConnection connection, SshAgentOptions agent, string arch, string staging, Func<RemoteCommand, bool, TimeSpan?, Task<RemoteResult>> run, Func<string, Task> log, CancellationToken cancellationToken)
    {
        if (!agent.Binaries.TryGetValue("linux-" + arch, out var source) || (string.IsNullOrWhiteSpace(source.Url) && string.IsNullOrWhiteSpace(source.LocalPath)))
            throw new SshBootstrapException(SshBootstrapCodes.NoBinary,
                $"No agent binary is configured for linux-{arch}. Set Aethera:Ssh:Agent:Binaries:linux-{arch}:Url and :Sha256, or :LocalPath for development.");

        var target = staging + "/aethera-agent";
        string expected;
        if (!string.IsNullOrWhiteSpace(source.LocalPath))
        {
            if (!File.Exists(source.LocalPath)) throw new SshBootstrapException(SshBootstrapCodes.NoBinary, "The configured local agent binary does not exist on the control plane host.");
            await using var file = File.OpenRead(source.LocalPath);
            expected = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            if (source.Sha256 is { Length: > 0 } configured && !string.Equals(configured, expected, StringComparison.OrdinalIgnoreCase))
                throw new SshBootstrapException(SshBootstrapCodes.ChecksumMismatch, "The local agent binary does not match the configured SHA-256.");
            file.Position = 0;
            await log($"Uploading the agent binary ({file.Length / 1024} KiB, sha256 {expected[..12]}...)");
            await connection.UploadAsync(target, file, "0700", cancellationToken);
        }
        else
        {
            var url = source.Url!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(options.Value.AllowInsecureAgentDownload && uri.Scheme == Uri.UriSchemeHttp)) || uri.UserInfo.Length > 0)
                throw new SshBootstrapException(SshBootstrapCodes.NoBinary, "The agent download URL must be an https URL without credentials.");
            if (source.Sha256 is not { } sha || !Sha256Pattern().IsMatch(sha))
                throw new SshBootstrapException(SshBootstrapCodes.NoBinary, "A download URL needs the binary's SHA-256 (Aethera:Ssh:Agent:Binaries:linux-" + arch + ":Sha256).");
            expected = sha.ToLowerInvariant();
            await log("Downloading the agent binary on the host");
            const string download = "set -eu; if command -v curl >/dev/null 2>&1; then curl -fsSL --proto '=https,http' --max-time 300 -o \"$2\" \"$1\"; elif command -v wget >/dev/null 2>&1; then wget -q -T 300 -O \"$2\" \"$1\"; else echo 'neither curl nor wget is installed' >&2; exit 127; fi; chmod 0700 \"$2\"";
            var fetched = await run(new RemoteCommand("sh -c " + ShellQuote.Quote(download) + " sh " + ShellQuote.Quote(uri.AbsoluteUri) + " " + ShellQuote.Quote(target)), false, TimeSpan.FromMinutes(6));
            if (!fetched.Succeeded) throw new SshBootstrapException(SshBootstrapCodes.Failed, "Downloading the agent failed: " + DockerErrors.Summarize(fetched.Stderr, fetched.Stdout));
        }

        // Verify what is on the host, whichever way it got there.
        var check = await run(new RemoteCommand("sha256sum " + ShellQuote.Quote(target)), false, null);
        var actual = check.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (!check.Succeeded || actual != expected)
            throw new SshBootstrapException(SshBootstrapCodes.ChecksumMismatch, "The agent binary on the host does not match the expected SHA-256; nothing was installed.");
        await log("The agent binary matches the expected SHA-256");
        return expected;
    }

    private async Task<string> IssueTokenAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var (token, _) = JoinTokenService.Issue(db, serverId, clock.UtcNow, TimeSpan.FromMinutes(options.Value.BootstrapJoinTokenMinutes));
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    private async Task RevokeUnusedTokenAsync(Guid serverId, string token)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var hash = JoinTokenService.Hash(token);
            var record = await db.JoinTokens.FirstOrDefaultAsync(t => t.ServerId == serverId && t.TokenHash == hash && t.UsedAt == null && t.RevokedAt == null, CancellationToken.None);
            if (record is null) return;
            record.Revoke(clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // The token expires on its own.
        }
    }
}
