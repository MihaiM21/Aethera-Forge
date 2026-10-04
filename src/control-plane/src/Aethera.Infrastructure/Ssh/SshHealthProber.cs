using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;

namespace Aethera.Infrastructure.Ssh;

/// <summary>
/// Health probes over SSH (ADR 0002): TCP and HTTP targets are reached through a <b>direct-tcpip</b> channel, so <c>localhost</c> and
/// container-network addresses resolve from the server's point of view. A container target reads Docker's own health status.
/// </summary>
public sealed class SshHealthProber
{
    /// <summary>The probe runs on the server's network: link-local addresses (cloud metadata) are never a valid target, as for the agent.</summary>
    public static void ValidateTarget(string host, int port)
    {
        SshValidators.Port(port, "The probe port");
        if (host.Length is 0 or > 253 || host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '?' or '#'))
            throw new ServerTransportException(TransportErrors.CommandRejected, "The probe host is not valid.");
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)) throw LinkLocal();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsLinkLocal(ip)) throw LinkLocal();
    }

    public static bool IsLinkLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static ServerTransportException LinkLocal() => new(TransportErrors.CommandRejected, "Link-local addresses (cloud metadata) cannot be probed.");

    public async Task<HealthProbeOutcome> ProbeAsync(ISshConnection connection, HealthProbeCommand command, Func<RemoteCommand, CancellationToken, Task<RemoteResult>> run, CancellationToken cancellationToken)
    {
        var timeout = command.Timeout ?? TimeSpan.FromSeconds(5);
        var interval = command.Interval ?? TimeSpan.FromSeconds(2);
        var retries = Math.Max(1, command.Retries);
        var successNeeded = Math.Max(1, command.SuccessThreshold);
        var startedAt = DateTimeOffset.UtcNow;
        var attempts = 0;
        var failures = 0;
        var successes = 0;
        (bool Ok, int Status, TimeSpan Latency, string Detail) last = (false, 0, TimeSpan.Zero, "not probed");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            var watch = Stopwatch.StartNew();
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(timeout);
            try
            {
                var (ok, status, detail) = await ProbeOnceAsync(connection, command.Target, run, attemptTimeout.Token);
                last = (ok, status, watch.Elapsed, detail);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = (false, 0, watch.Elapsed, $"timed out after {timeout.TotalSeconds:0.#} s");
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or HttpRequestException or Renci.SshNet.Common.SshException)
            {
                last = (false, 0, watch.Elapsed, "probe failed: " + ex.GetType().Name);
            }

            if (last.Ok)
            {
                failures = 0;
                if (++successes >= successNeeded) break;
            }
            else
            {
                successes = 0;
                // Failures inside the start period do not count (Docker's HEALTHCHECK semantics).
                if (DateTimeOffset.UtcNow - startedAt >= (command.StartPeriod ?? TimeSpan.Zero) && ++failures >= retries) break;
            }

            await Task.Delay(interval, cancellationToken);
        }

        return new HealthProbeOutcome(last.Ok, attempts, last.Status, last.Latency, last.Detail, DateTimeOffset.UtcNow);
    }

    private static async Task<(bool Ok, int Status, string Detail)> ProbeOnceAsync(
        ISshConnection connection, ProbeTarget target, Func<RemoteCommand, CancellationToken, Task<RemoteResult>> run, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case ContainerHealthProbeTarget container:
            {
                var result = await run(DockerCommands.ContainerHealth(container.Container), cancellationToken);
                if (!result.Succeeded) return (false, 0, DockerErrors.Summarize(result.Stderr, result.Stdout));
                var status = result.Stdout.Trim();
                return (status == "healthy", 0, status.Length > 0 ? $"container health: {status}" : "container health unknown");
            }

            case TcpProbeTarget tcp:
            {
                ValidateTarget(tcp.Host, tcp.Port);
                await using var stream = await connection.OpenTunnelAsync(tcp.Host, tcp.Port, cancellationToken);
                // The local end of a direct-tcpip forward accepts at once; a refused remote connection shows up as an immediate close.
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(TimeSpan.FromMilliseconds(400));
                try
                {
                    var buffer = new byte[1];
                    var read = await stream.ReadAsync(buffer, probe.Token);
                    return read == 0 ? (false, 0, "connection closed by the server") : (true, 0, "connected");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return (true, 0, "connected");
                }
                catch (IOException)
                {
                    return (false, 0, "connection refused");
                }
            }

            case HttpProbeTarget http:
                return await ProbeHttpAsync(connection, http, cancellationToken);

            default:
                throw new ServerTransportException(TransportErrors.CommandRejected, "The probe target is not supported.");
        }
    }

    private static async Task<(bool Ok, int Status, string Detail)> ProbeHttpAsync(ISshConnection connection, HttpProbeTarget target, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new ServerTransportException(TransportErrors.CommandRejected, "The probe URL must be an http(s) URL without credentials.");
        ValidateTarget(uri.Host, uri.Port);

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = target.FollowRedirects,
            UseProxy = false,
            ConnectCallback = async (context, token) => await connection.OpenTunnelAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token),
        };
        if (target.InsecureSkipVerify) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        using var request = new HttpRequestMessage(new HttpMethod(string.IsNullOrEmpty(target.Method) ? "GET" : target.Method.ToUpperInvariant()), uri);
        foreach (var (name, value) in target.Headers ?? new Dictionary<string, string>())
        {
            if (value.AsSpan().IndexOfAny('\r', '\n') >= 0 || !request.Headers.TryAddWithoutValidation(name, value))
                throw new ServerTransportException(TransportErrors.CommandRejected, "A probe header is not valid.");
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var status = (int)response.StatusCode;
        var expected = target.ExpectedStatus is { Count: > 0 } list ? list.Contains(status) : status is >= 200 and < 400;
        if (!expected) return (false, status, $"unexpected status {status}");
        if (target.BodyContains is { Length: > 0 } needle)
        {
            var body = await ReadBoundedAsync(response, cancellationToken);
            if (!body.Contains(needle, StringComparison.Ordinal)) return (false, status, "response body did not contain the expected text");
        }

        return (true, status, $"status {status}");
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
