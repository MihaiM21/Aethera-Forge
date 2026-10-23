using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aethera.Infrastructure.Registries;

public enum RegistryTestOutcome
{
    /// <summary>The registry answered the v2 API and accepted the credentials (or needed none).</summary>
    Ok,

    /// <summary>The registry is there but refused the credentials, or wants some and none are stored.</summary>
    Unauthorized,

    /// <summary>No usable answer: bad address, DNS, refused connection, TLS error, timeout, not a registry.</summary>
    Unreachable,
}

/// <summary>The verdict of a registry test. <see cref="Message"/> is for people and never contains the password, a token or the username.</summary>
public sealed record RegistryTestResult(RegistryTestOutcome Outcome, string Message, int? HttpStatus = null);

/// <summary>Checks, from the control plane, that a Docker registry is reachable and that stored credentials work.</summary>
public interface IRegistryConnectionTester
{
    Task<RegistryTestResult> TestAsync(string url, string? username, string? password, CancellationToken cancellationToken);
}

/// <summary>
/// Docker Registry HTTP API v2 check: <c>GET /v2/</c>; on <c>401</c> follow the <c>WWW-Authenticate</c> challenge, either <c>Bearer</c>
/// (the token service named by <c>realm</c> and <c>service</c> is asked for a token with the stored username and password, as Docker Hub,
/// GHCR and GitLab expect) or <c>Basic</c>.
/// <para><b>Outbound-call stance (SSRF).</b> The address is chosen by an administrator, but the control plane sits next to the database
/// and the Docker network, so the default connector refuses loopback, link-local (cloud metadata <c>169.254.169.254</c>), unspecified
/// and multicast addresses. Every address a name resolves to is checked at connect time (no DNS rebinding between check and use),
/// redirects are not followed, the token service must be https unless the registry itself is plain http on the same host, bodies are
/// capped and the whole test is bounded to ten seconds. Private LAN ranges stay allowed: a registry on a private network is normal.</para>
/// </summary>
public sealed partial class RegistryConnectionTester : IRegistryConnectionTester
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const int MaxBody = 64 * 1024;
    private static readonly string[] DockerHubHosts = ["docker.io", "index.docker.io", "registry.hub.docker.com", "registry-1.docker.io"];

    private readonly HttpClient _http;

    /// <summary>Production: a client whose connector enforces the address policy.</summary>
    public RegistryConnectionTester()
        : this(new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = GuardedConnectAsync,
        }) { Timeout = Timeout.InfiniteTimeSpan })
    {
    }

    /// <summary>Tests: any client (a fake handler). It must not follow redirects.</summary>
    public RegistryConnectionTester(HttpClient http) => _http = http;

    public async Task<RegistryTestResult> TestAsync(string url, string? username, string? password, CancellationToken cancellationToken)
    {
        if (!TryNormalize(url, out var baseUri))
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, "The registry address is not valid.");
        if (IPAddress.TryParse(baseUri.Host.Trim('[', ']'), out var literal) && !IsAllowed(literal))
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, "This address is not allowed (loopback, link-local or unspecified); a registry on this machine's own loopback cannot be tested from the control plane.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            return await RunAsync(baseUri, username, password, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, $"{baseUri.Authority} did not answer within {(int)Budget.TotalSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, Describe(baseUri, ex));
        }
    }

    private async Task<RegistryTestResult> RunAsync(Uri baseUri, string? username, string? password, CancellationToken ct)
    {
        var ping = new Uri(baseUri, "/v2/");
        var haveCredentials = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password);

        using var first = await SendAsync(ping, null, ct);
        var status = (int)first.StatusCode;
        if (first.IsSuccessStatusCode)
        {
            return new RegistryTestResult(RegistryTestOutcome.Ok,
                haveCredentials ? "Reachable. The registry answers without credentials, so the stored ones were not needed (and not checked)." : "Reachable; the registry answers without credentials.", status);
        }

        if (first.StatusCode != HttpStatusCode.Unauthorized)
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, Unexpected(baseUri, first), status);

        var challenge = ParseChallenge(first);
        switch (challenge.Scheme)
        {
            case "basic":
            {
                if (!haveCredentials)
                    return new RegistryTestResult(RegistryTestOutcome.Unauthorized, "The registry requires credentials and none are stored.", status);
                using var authed = await SendAsync(ping, BasicHeader(username!, password!), ct);
                return authed.IsSuccessStatusCode
                    ? new RegistryTestResult(RegistryTestOutcome.Ok, "Reachable; the stored credentials were accepted.", (int)authed.StatusCode)
                    : Rejected(baseUri, authed);
            }

            case "bearer":
                return await BearerAsync(baseUri, ping, challenge, haveCredentials ? BasicHeader(username!, password!) : null, ct);

            default:
                return new RegistryTestResult(RegistryTestOutcome.Unauthorized,
                    "The registry answered 401 without a usable WWW-Authenticate challenge (Bearer or Basic expected).", status);
        }
    }

    private async Task<RegistryTestResult> BearerAsync(Uri baseUri, Uri ping, Challenge challenge, AuthenticationHeaderValue? credentials, CancellationToken ct)
    {
        if (!Uri.TryCreate(challenge.Realm, UriKind.Absolute, out var realm) || realm.Scheme is not ("https" or "http") || realm.UserInfo.Length > 0)
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, "The registry named a token service address that is not valid.");
        // A password is only ever sent over TLS, or over plain http to the very host the user configured as plain http.
        if (realm.Scheme == "http" && !(baseUri.Scheme == "http" && string.Equals(realm.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)))
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, "The registry's token service is not https; refusing to send credentials to it.");
        if (IPAddress.TryParse(realm.Host.Trim('[', ']'), out var literal) && !IsAllowed(literal))
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, "The registry's token service address is not allowed.");

        var query = new StringBuilder();
        if (!string.IsNullOrEmpty(challenge.Service)) query.Append("service=").Append(Uri.EscapeDataString(challenge.Service));
        if (!string.IsNullOrEmpty(challenge.Scope)) query.Append(query.Length > 0 ? "&" : "").Append("scope=").Append(Uri.EscapeDataString(challenge.Scope));
        var tokenUri = new UriBuilder(realm) { Query = realm.Query.Length > 1 ? realm.Query.TrimStart('?') + (query.Length > 0 ? "&" : "") + query : query.ToString() }.Uri;

        using var tokenResponse = await SendAsync(tokenUri, credentials, ct);
        var tokenStatus = (int)tokenResponse.StatusCode;
        if (tokenResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new RegistryTestResult(RegistryTestOutcome.Unauthorized,
                credentials is null ? "The registry requires credentials and none are stored." : "The token service rejected the stored username and password.", tokenStatus);
        }

        if (!tokenResponse.IsSuccessStatusCode)
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, $"The token service of {baseUri.Authority} answered {tokenStatus}.", tokenStatus);

        var token = await ReadTokenAsync(tokenResponse, ct);
        if (token is null)
            return new RegistryTestResult(RegistryTestOutcome.Unreachable, "The token service answered without a token.", tokenStatus);

        using var authed = await SendAsync(ping, new AuthenticationHeaderValue("Bearer", token), ct);
        if (authed.IsSuccessStatusCode)
        {
            return new RegistryTestResult(RegistryTestOutcome.Ok,
                credentials is null ? "Reachable; the registry hands out anonymous tokens (public images only)." : "Reachable; the stored credentials were accepted.", (int)authed.StatusCode);
        }

        return authed.StatusCode == HttpStatusCode.Unauthorized
            ? new RegistryTestResult(RegistryTestOutcome.Unauthorized, "The registry rejected the token it had just issued.", 401)
            : new RegistryTestResult(RegistryTestOutcome.Unreachable, Unexpected(baseUri, authed), (int)authed.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, AuthenticationHeaderValue? authorization, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Aethera-registry-test");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = authorization;
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<string?> ReadTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBody];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }

        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, total));
            foreach (var name in new[] { "token", "access_token" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()))
                    return value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static RegistryTestResult Rejected(Uri baseUri, HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new RegistryTestResult(RegistryTestOutcome.Unauthorized, "The registry rejected the stored username and password.", (int)response.StatusCode)
            : new RegistryTestResult(RegistryTestOutcome.Unreachable, Unexpected(baseUri, response), (int)response.StatusCode);

    private static string Unexpected(Uri baseUri, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status switch
        {
            404 => $"{baseUri.Authority} answered 404 on /v2/: it does not look like a Docker registry.",
            >= 300 and < 400 => $"{baseUri.Authority} redirects /v2/ (HTTP {status}); redirects are not followed. Use the address the registry redirects to.",
            >= 500 => $"{baseUri.Authority} answered HTTP {status} (a server error on the registry's side).",
            _ => $"{baseUri.Authority} answered HTTP {status} on /v2/.",
        };
    }

    private static string Describe(Uri baseUri, HttpRequestException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException)
                return $"The TLS certificate of {baseUri.Authority} is not trusted or does not match the name.";
            if (e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData })
                return $"{baseUri.Host} does not resolve.";
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return $"{baseUri.Authority} refused the connection.";
            if (e is SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.NetworkUnreachable or SocketError.HostUnreachable })
                return $"{baseUri.Authority} is not reachable from the control plane.";
        }

        // Only the message of the exceptions raised by this class (the address policy) is safe to show verbatim; anything else is generic.
        return ex.Message.StartsWith("The registry address", StringComparison.Ordinal)
            ? $"{baseUri.Host}: {ex.Message}"
            : $"Could not connect to {baseUri.Authority}.";
    }

    /// <summary>Registry address as typed (host, host:port, or URL) to the origin of its v2 API. Docker Hub names map to registry-1.docker.io.</summary>
    public static bool TryNormalize(string? url, out Uri baseUri)
    {
        baseUri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Host.Length == 0)
            return false;
        var host = uri.Host;
        if (DockerHubHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) host = "registry-1.docker.io";
        var builder = new UriBuilder(uri.Scheme, host, uri.IsDefaultPort || host != uri.Host ? -1 : uri.Port);
        baseUri = builder.Uri;
        return true;
    }

    private static AuthenticationHeaderValue BasicHeader(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    private sealed record Challenge(string Scheme, string? Realm, string? Service, string? Scope);

    private static Challenge ParseChallenge(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            var scheme = header.Scheme.ToLowerInvariant();
            if (scheme == "basic") return new Challenge("basic", null, null, null);
            if (scheme != "bearer" || header.Parameter is not { } parameter) continue;
            string? realm = null, service = null, scope = null;
            foreach (Match m in ChallengeParameter().Matches(parameter))
            {
                var value = m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["plain"].Value;
                switch (m.Groups["key"].Value.ToLowerInvariant())
                {
                    case "realm": realm = value; break;
                    case "service": service = value; break;
                    case "scope": scope = value; break;
                }
            }

            return new Challenge("bearer", realm, service, scope);
        }

        return new Challenge("", null, null, null);
    }

    [GeneratedRegex("(?<key>[A-Za-z_]+)=(?:\"(?<quoted>[^\"]*)\"|(?<plain>[^,\\s]*))")]
    private static partial Regex ChallengeParameter();

    // ---- address policy ----------------------------------------------------------------------------------------------------------

    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast) return false;
            var bytes = address.GetAddressBytes();
            return !(bytes[0] == 0xfd && bytes[1] == 0x00 && bytes[2] == 0x0e && bytes[3] == 0xc2); // AWS IPv6 metadata fd00:ec2::254
        }

        var b = address.GetAddressBytes();
        if (b[0] == 169 && b[1] == 254) return false; // link-local, cloud metadata
        if (b[0] == 0 || b[0] >= 224) return false;   // "this network", multicast, reserved, broadcast
        return true;
    }

    private static async ValueTask<Stream> GuardedConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsAllowed).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("The registry address is not allowed (it resolves to a loopback, link-local or unspecified address).");

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException("Could not connect to the registry.", last);
    }
}
