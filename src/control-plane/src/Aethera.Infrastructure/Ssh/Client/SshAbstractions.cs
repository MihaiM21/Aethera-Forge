using Aethera.Infrastructure.Ssh.Docker;

namespace Aethera.Infrastructure.Ssh.Client;

/// <summary>Where and as whom to connect.</summary>
public sealed record SshTarget(string Host, int Port, string User)
{
    public override string ToString() => $"{User}@{Host}:{Port}";
}

/// <summary>
/// The material to authenticate with. <see cref="ToString"/> and the record's printing never reveal it (ADR 0002 "Secret-handling rules for code").
/// </summary>
public sealed record SshAuth
{
    // SECRET: never log
    public string? Password { get; init; }

    // SECRET: never log
    public string? PrivateKey { get; init; }

    // SECRET: never log
    public string? Passphrase { get; init; }

    public string Kind => PrivateKey is not null ? "key" : "password";

    public override string ToString() => $"SshAuth({Kind}: [secret])";

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind).Append(", Secret = [secret]");
        return true;
    }
}

/// <summary>A host key the server presented: algorithm name and its OpenSSH-style SHA-256 fingerprint (<c>SHA256:base64</c>).</summary>
public sealed record HostKeyInfo(string Algorithm, string Fingerprint);

/// <summary>One line of a remote command's output.</summary>
public readonly record struct RemoteLine(bool IsStderr, string Text);

/// <summary>The terminal state of a remote command: exit status and captured output (bounded; see <see cref="Truncated"/>).</summary>
public sealed record RemoteResult(int ExitStatus, string Stdout, string Stderr, bool Truncated = false)
{
    public bool Succeeded => ExitStatus == 0;
}

/// <summary>Per-call settings of <see cref="ISshConnection.ExecuteAsync"/>.</summary>
public sealed class RemoteExecOptions
{
    public TimeSpan? Timeout { get; init; }

    /// <summary>Called for each output line as it arrives (build and pull progress). The text is masked of the command's secrets by the caller.</summary>
    public Action<RemoteLine>? OnLine { get; init; }

    /// <summary>Bytes of standard output kept in <see cref="RemoteResult.Stdout"/>.</summary>
    public int MaxStdoutBytes { get; init; } = 16 * 1024 * 1024;

    public int MaxStderrBytes { get; init; } = 256 * 1024;
}

/// <summary>The server presented a different host key than the pinned one. The connection is not used until a user confirms the new key.</summary>
public sealed class SshHostKeyChangedException(string pinned, HostKeyInfo presented)
    : Exception("The SSH host key of this server changed. Confirm the new fingerprint before Aethera connects again.")
{
    public string PinnedFingerprint { get; } = pinned;

    public HostKeyInfo Presented { get; } = presented;
}

/// <summary>Connecting or authenticating failed (the reason carries no credentials).</summary>
public sealed class SshConnectException(string message, Exception? inner = null, bool authentication = false) : Exception(message, inner)
{
    /// <summary>True when the server answered but refused (or could not use) the credentials.</summary>
    public bool Authentication { get; } = authentication;
}

/// <summary>An established SSH session: command channels (multiplexed), file upload and port forwarding.</summary>
public interface ISshConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    HostKeyInfo HostKey { get; }

    /// <summary>Runs one command on its own channel and waits for it.</summary>
    Task<RemoteResult> ExecuteAsync(RemoteCommand command, RemoteExecOptions options, CancellationToken cancellationToken);

    /// <summary>Runs a command and yields its output lines while it runs; ends when the command ends. Cancel to stop it.</summary>
    IAsyncEnumerable<RemoteLine> StreamAsync(RemoteCommand command, CancellationToken cancellationToken);

    /// <summary>Writes a file (mode as an octal string, e.g. <c>0600</c>) atomically; the content never appears in a command line.</summary>
    Task UploadAsync(string remotePath, Stream content, string mode, CancellationToken cancellationToken);

    /// <summary>Opens a TCP stream to <paramref name="host"/>:<paramref name="port"/> as seen from the server (SSH direct-tcpip).</summary>
    Task<Stream> OpenTunnelAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>Opens SSH connections. Replaced by a scripted fake in unit tests.</summary>
public interface ISshConnector
{
    /// <param name="expectedFingerprint">The pinned fingerprint, or null on first contact (the presented key is accepted and reported in <c>HostKey</c>).</param>
    /// <exception cref="SshHostKeyChangedException">The presented key differs from <paramref name="expectedFingerprint"/>.</exception>
    /// <exception cref="SshConnectException">The server could not be reached or refused the credentials.</exception>
    Task<ISshConnection> ConnectAsync(SshTarget target, SshAuth auth, string? expectedFingerprint, SshConnectionSettings settings, CancellationToken cancellationToken);

    /// <summary>Reads the host key a server presents during key exchange and disconnects without authenticating (the "show me the fingerprint" step of the add-server wizard).</summary>
    Task<HostKeyInfo> ScanHostKeyAsync(string host, int port, SshConnectionSettings settings, CancellationToken cancellationToken);
}

public sealed record SshConnectionSettings(TimeSpan ConnectTimeout, TimeSpan KeepAliveInterval);
