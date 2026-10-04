using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Aethera.Infrastructure.Ssh.Docker;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Aethera.Infrastructure.Ssh.Client;

/// <summary><see cref="ISshConnector"/> over SSH.NET.</summary>
public sealed class SshNetConnector : ISshConnector
{
    public async Task<ISshConnection> ConnectAsync(
        SshTarget target, SshAuth auth, string? expectedFingerprint, SshConnectionSettings settings, CancellationToken cancellationToken)
    {
        var methods = BuildMethods(target.User, auth, out var keyFile);
        var info = new ConnectionInfo(target.Host, target.Port, target.User, methods) { Timeout = settings.ConnectTimeout };
        var client = new SshClient(info) { KeepAliveInterval = settings.KeepAliveInterval };

        HostKeyInfo? presented = null;
        var mismatch = false;
        client.HostKeyReceived += (_, e) =>
        {
            presented = new HostKeyInfo(e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
            e.CanTrust = expectedFingerprint is null || FingerprintsEqual(expectedFingerprint, presented.Fingerprint);
            mismatch = !e.CanTrust;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            keyFile?.Dispose();
            if (ex is OperationCanceledException) throw;
            if (mismatch && presented is not null && expectedFingerprint is not null) throw new SshHostKeyChangedException(expectedFingerprint, presented);
            throw Translate(ex);
        }

        keyFile?.Dispose();
        return new SshNetConnection(client, presented ?? new HostKeyInfo("unknown", "unknown"));
    }

    private static bool FingerprintsEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static AuthenticationMethod[] BuildMethods(string user, SshAuth auth, out PrivateKeyFile? keyFile)
    {
        keyFile = null;
        var methods = new List<AuthenticationMethod>();
        if (auth.PrivateKey is { Length: > 0 } pem)
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(pem));
                keyFile = string.IsNullOrEmpty(auth.Passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, auth.Passphrase);
            }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException or CryptographicException)
            {
                throw new SshConnectException("The stored SSH private key could not be read (wrong format or passphrase).", null, authentication: true);
            }

            methods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
        }

        if (auth.Password is { Length: > 0 } password)
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            methods.Add(interactive);
        }

        if (methods.Count == 0) throw new SshConnectException("The server has no usable SSH credential.", null, authentication: true);
        return [.. methods];
    }

    private static SshConnectException Translate(Exception ex) => ex switch
    {
        SshAuthenticationException => new SshConnectException("The server refused the SSH credentials.", ex, authentication: true),
        SshOperationTimeoutException => new SshConnectException("The SSH connection timed out.", ex),
        SocketException socket => new SshConnectException($"The server could not be reached ({socket.SocketErrorCode}).", ex),
        SshConnectionException connection => new SshConnectException("The SSH connection failed: " + connection.Message, ex),
        SshException => new SshConnectException("The SSH connection failed.", ex),
        _ => new SshConnectException("The SSH connection failed (" + ex.GetType().Name + ").", ex),
    };
}

/// <summary>One SSH.NET session; every command, upload and tunnel gets its own channel on it.</summary>
internal sealed class SshNetConnection(SshClient client, HostKeyInfo hostKey) : ISshConnection
{
    public bool IsConnected => client.IsConnected;

    public HostKeyInfo HostKey => hostKey;

    public async Task<RemoteResult> ExecuteAsync(RemoteCommand command, RemoteExecOptions options, CancellationToken cancellationToken)
    {
        using var ssh = client.CreateCommand(command.Line, Encoding.UTF8);
        ssh.CommandTimeout = options.Timeout ?? Timeout.InfiniteTimeSpan;

        var stdout = new BoundedText(options.MaxStdoutBytes);
        var stderr = new BoundedText(options.MaxStderrBytes);
        var run = ssh.ExecuteAsync(cancellationToken);
        var feeding = FeedAsync(ssh, command.Stdin, cancellationToken);
        var outTask = PumpAsync(ssh.OutputStream, stderr: false, stdout, options.OnLine);
        var errTask = PumpAsync(ssh.ExtendedOutputStream, stderr: true, stderr, options.OnLine);

        try
        {
            await run.ConfigureAwait(false);
            await feeding.ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        }
        catch (SshOperationTimeoutException)
        {
            throw new TimeoutException("The remote command timed out.");
        }

        return new RemoteResult(ssh.ExitStatus ?? -1, stdout.ToString(), stderr.ToString(), stdout.Truncated || stderr.Truncated);
    }

    public async IAsyncEnumerable<RemoteLine> StreamAsync(RemoteCommand command, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<RemoteLine>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        using var ssh = client.CreateCommand(command.Line, Encoding.UTF8);
        ssh.CommandTimeout = Timeout.InfiniteTimeSpan;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var run = ssh.ExecuteAsync(cts.Token);
        var feeding = FeedAsync(ssh, command.Stdin, cts.Token);
        var pumps = Task.WhenAll(
            PumpLinesAsync(ssh.OutputStream, false, channel.Writer, cts.Token),
            PumpLinesAsync(ssh.ExtendedOutputStream, true, channel.Writer, cts.Token));
        _ = Task.Run(async () =>
        {
            try
            {
                await run.ConfigureAwait(false);
                await feeding.ConfigureAwait(false);
                await pumps.ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return line;
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try { ssh.CancelAsync(); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or SshException) { /* already finished */ }
        }
    }

    public async Task UploadAsync(string remotePath, Stream content, string mode, CancellationToken cancellationToken)
    {
        if (mode.Length is < 3 or > 4 || !mode.All(c => c is >= '0' and <= '7')) throw new ArgumentException("The file mode must be octal.", nameof(mode));
        // Created under umask 077 and renamed into place: a file is never visible with looser permissions or half written.
        const string script = "set -e; umask 077; t=\"$1.tmp.$$\"; trap 'rm -f \"$t\"' EXIT; cat > \"$t\"; chmod \"$2\" \"$t\"; mv -f \"$t\" \"$1\"";
        using var ssh = client.CreateCommand("sh -c " + ShellQuote.Quote(script) + " sh " + ShellQuote.Quote(remotePath) + " " + ShellQuote.Quote(mode), Encoding.UTF8);
        var stderr = new BoundedText(8192);
        var run = ssh.ExecuteAsync(cancellationToken);
        var errTask = PumpAsync(ssh.ExtendedOutputStream, stderr: true, stderr, null);
        var outTask = PumpAsync(ssh.OutputStream, stderr: false, new BoundedText(1024), null);
        await using (var input = ssh.CreateInputStream())
        {
            await content.CopyToAsync(input, 64 * 1024, cancellationToken).ConfigureAwait(false);
        }

        await run.ConfigureAwait(false);
        await Task.WhenAll(errTask, outTask).ConfigureAwait(false);
        if (ssh.ExitStatus != 0) throw new IOException($"Uploading a file failed (exit {ssh.ExitStatus}): {stderr.ToString().Trim()}");
    }

    public async Task<Stream> OpenTunnelAsync(string host, int port, CancellationToken cancellationToken)
    {
        var forward = new ForwardedPortLocal("127.0.0.1", 0, host, (uint)port);
        client.AddForwardedPort(forward);
        TcpClient? tcp = null;
        try
        {
            forward.Start();
            tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", (int)forward.BoundPort, cancellationToken).ConfigureAwait(false);
            return new TunnelStream(tcp, forward, client);
        }
        catch
        {
            tcp?.Dispose();
            Cleanup(forward, client);
            throw;
        }
    }

    internal static void Cleanup(ForwardedPortLocal forward, SshClient owner)
    {
        try { forward.Stop(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { /* session already gone */ }
        try { owner.RemoveForwardedPort(forward); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { /* session already gone */ }
        forward.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected) client.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException)
        {
            // Closing a dead session.
        }

        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task FeedAsync(SshCommand ssh, string? stdin, CancellationToken cancellationToken)
    {
        if (stdin is null) return;
        await using var input = ssh.CreateInputStream();
        var bytes = Encoding.UTF8.GetBytes(stdin);
        await input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream stream, bool stderr, BoundedText sink, Action<RemoteLine>? onLine)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            sink.AppendLine(line);
            onLine?.Invoke(new RemoteLine(stderr, line));
        }
    }

    private static async Task PumpLinesAsync(Stream stream, bool stderr, ChannelWriter<RemoteLine> writer, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                await writer.WriteAsync(new RemoteLine(stderr, line), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The consumer stopped.
        }
    }

    /// <summary>Output kept up to a byte budget; the rest is dropped and flagged.</summary>
    private sealed class BoundedText(int maxBytes)
    {
        private readonly StringBuilder _text = new();
        private int _bytes;

        public bool Truncated { get; private set; }

        public void AppendLine(string line)
        {
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            lock (_text)
            {
                if (_bytes + size > maxBytes)
                {
                    Truncated = true;
                    return;
                }

                _bytes += size;
                _text.Append(line).Append('\n');
            }
        }

        public override string ToString()
        {
            lock (_text) return _text.ToString();
        }
    }

    /// <summary>The local end of a direct-tcpip forward; disposing it closes the forward too.</summary>
    private sealed class TunnelStream(TcpClient tcp, ForwardedPortLocal forward, SshClient owner) : Stream
    {
        private readonly NetworkStream _inner = tcp.GetStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tcp.Dispose();
                Cleanup(forward, owner);
            }

            base.Dispose(disposing);
        }
    }
}
