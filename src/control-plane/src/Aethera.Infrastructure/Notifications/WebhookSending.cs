using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Notifications;

/// <summary>A webhook delivery that failed. The message never contains the signing secret.</summary>
public sealed class WebhookSendException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Which URLs a notification webhook may call (the SSRF rules, ADR 0010): <c>https</c> only and no loopback, private, link-local or
/// metadata addresses, unless <see cref="NotificationOptions.AllowPrivateWebhookTargets"/> allows <c>http</c> and internal targets.
/// </summary>
public static class WebhookTargetPolicy
{
    /// <summary>Null when <paramref name="url"/> may be called, else the reason (safe to show).</summary>
    public static string? Check(string? url, bool allowPrivate)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "The URL is not a valid absolute URL.";
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return "Only http(s) URLs are allowed.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "The URL must not contain credentials.";
        if (uri.Scheme == Uri.UriSchemeHttp && !allowPrivate) return "Webhook URLs must use https.";
        if (allowPrivate) return null;

        var host = uri.Host.Trim('[', ']');
        if (host.Length == 0) return "The URL has no host.";
        if (IPAddress.TryParse(host, out var address)) return IsPublic(address) ? null : "The URL points to a private, loopback or link-local address.";
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.'))
            return "The URL points to a local host name.";
        return null;
    }

    /// <summary>False for loopback, private (RFC 1918, ULA), link-local (including cloud metadata), CGNAT, multicast and unspecified addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || b[0] == 0
                || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
        }

        return false;
    }
}

/// <summary>
/// Posts a signed JSON body to a webhook URL: <c>X-Aethera-Event</c>, <c>X-Aethera-Delivery</c> and <c>X-Aethera-Signature: sha256=&lt;hex HMAC-SHA256
/// of the raw body with the channel secret&gt;</c>. 10 s timeout, redirects are not followed.
/// </summary>
public sealed class WebhookSender(HttpClient http, IOptions<NotificationOptions> options)
{
    public static string Sign(string body, string secret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    /// <summary>The HTTP client of production: no redirects, and every connection is checked against <see cref="WebhookTargetPolicy.IsPublic"/> after DNS resolution.</summary>
    public static HttpClient CreateClient(NotificationOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, options.WebhookTimeoutSeconds)),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                var allowed = options.AllowPrivateWebhookTargets ? addresses : addresses.Where(WebhookTargetPolicy.IsPublic).ToArray();
                if (allowed.Length == 0) throw new HttpRequestException("The host resolves only to private, loopback or link-local addresses.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <exception cref="WebhookSendException">The endpoint did not accept the delivery.</exception>
    public async Task SendAsync(string url, string eventType, Guid deliveryId, string body, string secret, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (WebhookTargetPolicy.Check(url, settings.AllowPrivateWebhookTargets) is { } refusal)
            throw new WebhookSendException(refusal, retryable: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Aethera-Event", eventType);
        request.Headers.TryAddWithoutValidation("X-Aethera-Delivery", deliveryId.ToString());
        request.Headers.TryAddWithoutValidation("X-Aethera-Signature", Sign(body, secret));
        request.Headers.UserAgent.ParseAdd("Aethera-Notifications/1");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.WebhookTimeoutSeconds)));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) return;
            if (status is >= 300 and < 400)
                throw new WebhookSendException($"The endpoint answered {status} (a redirect). Redirects are not followed; use the final URL.", retryable: false);
            throw new WebhookSendException($"The endpoint answered {status}.", retryable: status is 408 or 429 or >= 500);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new WebhookSendException($"The endpoint did not answer within {settings.WebhookTimeoutSeconds:0.#} seconds.", retryable: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new WebhookSendException("Could not reach the endpoint: " + (ex.InnerException?.Message ?? ex.Message), retryable: !IsPolicyRefusal(ex), ex);
        }
    }

    private static bool IsPolicyRefusal(HttpRequestException ex) => ex.Message.Contains("private, loopback", StringComparison.Ordinal)
        || ex.InnerException?.Message.Contains("private, loopback", StringComparison.Ordinal) == true;
}
