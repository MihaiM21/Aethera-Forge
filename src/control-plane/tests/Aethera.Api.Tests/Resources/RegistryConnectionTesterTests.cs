using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aethera.Infrastructure.Registries;

namespace Aethera.Api.Tests.Resources;

/// <summary>The registry test against a fake HTTP handler: anonymous, Bearer challenge (Docker Hub, GHCR, GitLab style), Basic, 401, unreachable.</summary>
public sealed class RegistryConnectionTesterTests
{
    private const string Password = "pw-marker-1234";
    private const string User = "ci-bot";

    private static string BasicOf(string user, string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (RegistryConnectionTester Tester, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new RegistryConnectionTester(new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string? challenge = null, string? body = null)
    {
        var response = new HttpResponseMessage(code);
        if (challenge is not null) response.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
        if (body is not null) response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return response;
    }

    private const string HubChallenge = "Bearer realm=\"https://auth.example.test/token\",service=\"registry.example.test\"";

    /// <summary>A Docker-Hub-like registry: anonymous /v2/ is 401 with a Bearer challenge; the token endpoint wants Basic credentials for private access.</summary>
    private static HttpResponseMessage BearerRegistry(HttpRequestMessage r, bool acceptCredentials = true, bool anonymousTokens = false)
    {
        if (r.RequestUri!.Host == "auth.example.test")
        {
            var basic = r.Headers.Authorization is { Scheme: "Basic" } a ? a.Parameter : null;
            if (basic == BasicOf(User, Password) && acceptCredentials) return Status(HttpStatusCode.OK, body: "{\"token\":\"tok-123\"}");
            if (basic is null && anonymousTokens) return Status(HttpStatusCode.OK, body: "{\"access_token\":\"anon-1\"}");
            return Status(HttpStatusCode.Unauthorized, body: "{\"errors\":[{\"code\":\"UNAUTHORIZED\"}]}");
        }

        return r.Headers.Authorization is { Scheme: "Bearer", Parameter: "tok-123" or "anon-1" }
            ? Status(HttpStatusCode.OK)
            : Status(HttpStatusCode.Unauthorized, HubChallenge);
    }

    [Fact]
    public async Task AnonymousRegistry_IsOk()
    {
        var (tester, handler) = Create(_ => Status(HttpStatusCode.OK));
        var result = await tester.TestAsync("registry.example.test:5000", null, null, default);

        Assert.Equal(RegistryTestOutcome.Ok, result.Outcome);
        Assert.Equal("https://registry.example.test:5000/v2/", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task BearerChallenge_ExchangesTheStoredCredentialsForAToken()
    {
        var (tester, handler) = Create(r => BearerRegistry(r));
        var result = await tester.TestAsync("https://registry.example.test", User, Password, default);

        Assert.Equal(RegistryTestOutcome.Ok, result.Outcome);
        Assert.Contains("credentials were accepted", result.Message);
        var tokenRequest = handler.Requests.Single(r => r.RequestUri!.Host == "auth.example.test");
        Assert.Equal("service=registry.example.test", tokenRequest.RequestUri!.Query.TrimStart('?'));
        Assert.Equal(BasicOf(User, Password), tokenRequest.Headers.Authorization!.Parameter);
        Assert.Equal("tok-123", handler.Requests.Last().Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task BearerChallenge_WithWrongCredentials_IsUnauthorized_AndTheMessageHasNoSecret()
    {
        var (tester, _) = Create(r => BearerRegistry(r, acceptCredentials: false));
        var result = await tester.TestAsync("registry.example.test", User, Password, default);

        Assert.Equal(RegistryTestOutcome.Unauthorized, result.Outcome);
        Assert.Equal(401, result.HttpStatus);
        Assert.DoesNotContain(Password, result.Message);
        Assert.DoesNotContain(User, result.Message);
    }

    [Fact]
    public async Task BearerChallenge_WithoutCredentials_IsUnauthorized_OrAnonymousWhenTheRegistryHandsOutTokens()
    {
        var (strict, _) = Create(r => BearerRegistry(r));
        var denied = await strict.TestAsync("registry.example.test", null, null, default);
        Assert.Equal(RegistryTestOutcome.Unauthorized, denied.Outcome);
        Assert.Contains("none are stored", denied.Message);

        var (hub, _) = Create(r => BearerRegistry(r, anonymousTokens: true));
        var anonymous = await hub.TestAsync("registry.example.test", null, null, default);
        Assert.Equal(RegistryTestOutcome.Ok, anonymous.Outcome);
        Assert.Contains("anonymous", anonymous.Message);
    }

    [Fact]
    public async Task BearerChallenge_ParametersWithScopeAndExistingQuery_AreCarriedToTheTokenService()
    {
        var (tester, handler) = Create(r => r.RequestUri!.Host == "auth.example.test"
            ? Status(HttpStatusCode.OK, body: "{\"token\":\"tok-123\"}")
            : r.Headers.Authorization is { Scheme: "Bearer" } ? Status(HttpStatusCode.OK)
            : Status(HttpStatusCode.Unauthorized, "Bearer realm=\"https://auth.example.test/token?account=x\",service=\"a b\",scope=\"registry:catalog:*\""));
        var result = await tester.TestAsync("registry.example.test", User, Password, default);

        Assert.Equal(RegistryTestOutcome.Ok, result.Outcome);
        var query = handler.Requests.Single(r => r.RequestUri!.Host == "auth.example.test").RequestUri!.Query;
        Assert.Equal("?account=x&service=a%20b&scope=registry%3Acatalog%3A%2A", query);
    }

    [Fact]
    public async Task BasicChallenge_IsCheckedWithTheStoredCredentials()
    {
        static HttpResponseMessage Respond(HttpRequestMessage r) =>
            r.Headers.Authorization?.Parameter == BasicOf(User, Password) ? Status(HttpStatusCode.OK) : Status(HttpStatusCode.Unauthorized, "Basic realm=\"registry\"");

        var (good, _) = Create(Respond);
        Assert.Equal(RegistryTestOutcome.Ok, (await good.TestAsync("registry.example.test", User, Password, default)).Outcome);

        var (bad, _) = Create(Respond);
        Assert.Equal(RegistryTestOutcome.Unauthorized, (await bad.TestAsync("registry.example.test", User, "other", default)).Outcome);
        Assert.Equal(RegistryTestOutcome.Unauthorized, (await bad.TestAsync("registry.example.test", null, null, default)).Outcome);
    }

    [Fact]
    public async Task A401WithoutAChallenge_IsUnauthorized()
    {
        var (tester, _) = Create(_ => Status(HttpStatusCode.Unauthorized));
        Assert.Equal(RegistryTestOutcome.Unauthorized, (await tester.TestAsync("registry.example.test", User, Password, default)).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "does not look like a Docker registry")]
    [InlineData(HttpStatusCode.Moved, "redirects")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "server error")]
    public async Task OtherStatuses_AreUnreachableWithAClearMessage(HttpStatusCode code, string expected)
    {
        var (tester, _) = Create(_ => Status(code));
        var result = await tester.TestAsync("registry.example.test", null, null, default);

        Assert.Equal(RegistryTestOutcome.Unreachable, result.Outcome);
        Assert.Contains(expected, result.Message);
        Assert.Equal((int)code, result.HttpStatus);
    }

    [Fact]
    public async Task ConnectionFailures_AreUnreachable()
    {
        var (refused, _) = Create(_ => throw new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
        var result = await refused.TestAsync("registry.example.test:5000", User, Password, default);
        Assert.Equal(RegistryTestOutcome.Unreachable, result.Outcome);
        Assert.Contains("refused the connection", result.Message);

        var (dns, _) = Create(_ => throw new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));
        Assert.Contains("does not resolve", (await dns.TestAsync("nope.example.test", null, null, default)).Message);

        var (tls, _) = Create(_ => throw new HttpRequestException("x", new System.Security.Authentication.AuthenticationException("bad cert")));
        Assert.Contains("TLS certificate", (await tls.TestAsync("registry.example.test", null, null, default)).Message);
    }

    [Fact]
    public async Task ATokenServiceOverPlainHttpOnAnotherHost_NeverReceivesTheCredentials()
    {
        var (tester, handler) = Create(_ => Status(HttpStatusCode.Unauthorized, "Bearer realm=\"http://evil.example.test/token\",service=\"x\""));
        var result = await tester.TestAsync("registry.example.test", User, Password, default);

        Assert.Equal(RegistryTestOutcome.Unreachable, result.Outcome);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host == "evil.example.test");
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://169.254.169.254")]
    [InlineData("https://[::1]:5000")]
    [InlineData("not a host")]
    [InlineData("ftp://registry.example.test")]
    [InlineData("")]
    public async Task DisallowedOrInvalidAddresses_AreRefusedWithoutAnyRequest_WhenLiteral(string address)
    {
        var (tester, handler) = Create(_ => Status(HttpStatusCode.OK));
        var result = await tester.TestAsync(address, null, null, default);

        Assert.Equal(RegistryTestOutcome.Unreachable, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700::1111", true)]
    public void AddressPolicy(string address, bool allowed) =>
        Assert.Equal(allowed, RegistryConnectionTester.IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("ghcr.io", "https://ghcr.io/")]
    [InlineData("registry.example.com:5000", "https://registry.example.com:5000/")]
    [InlineData("http://registry.example.com:5000/v2", "http://registry.example.com:5000/")]
    [InlineData("https://index.docker.io/v1/", "https://registry-1.docker.io/")]
    [InlineData("docker.io", "https://registry-1.docker.io/")]
    public void Normalize(string input, string expected)
    {
        Assert.True(RegistryConnectionTester.TryNormalize(input, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Fact]
    public async Task BasicHeaderParsing_HandlesMixedCaseAndMultipleChallenges()
    {
        var (tester, _) = Create(r =>
        {
            var response = Status(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Digest", "realm=\"x\""));
            if (r.Headers.Authorization is null) response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("BASIC", "realm=\"x\""));
            return r.Headers.Authorization?.Parameter == BasicOf(User, Password) ? Status(HttpStatusCode.OK) : response;
        });
        Assert.Equal(RegistryTestOutcome.Ok, (await tester.TestAsync("registry.example.test", User, Password, default)).Outcome);
    }
}
