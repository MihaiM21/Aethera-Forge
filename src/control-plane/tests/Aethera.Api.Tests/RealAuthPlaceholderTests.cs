using System.Net;
using Aethera.Domain;
using Aethera.Infrastructure;

namespace Aethera.Api.Tests;

/// <summary>
/// The pipeline with the real authentication wiring (policy scheme "Aethera" and WP1.1's two registration points) rather than
/// <see cref="TestAuthHandler"/>. WP1.1 replaces the placeholder handlers; these expectations must keep holding.
/// </summary>
public sealed class RealAuthPlaceholderTests : IClassFixture<RealAuthApiFactory>
{
    private readonly HttpClient _client;

    public RealAuthPlaceholderTests(RealAuthApiFactory factory) =>
        _client = factory.WithEndpoints(TestApi.Map, TestApi.Services).CreateClient();

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer aeth_notarealtoken")]
    public async Task WithoutValidCredentials_Is401Json_NeverARedirect(string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_test/authenticated");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        var response = await _client.SendAsync(request);

        response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.unauthenticated");
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task TestAuthenticationHeaders_AreIgnored_OutsideTheTestAuthFactory()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_test/role/owner");
        request.Headers.Add(TestAuthHandler.RoleHeader, "Owner");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task AnonymousEndpointsAndOperationalEndpoints_StayReachable()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/_test/anonymous")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
    }
}

public sealed class ClockTests
{
    [Fact]
    public void SystemClock_IsUtc_AndFollowsTheTimeProvider()
    {
        Assert.Equal(TimeSpan.Zero, new SystemClock().UtcNow.Offset);

        var fixedTime = new DateTimeOffset(2026, 10, 3, 14, 7, 31, TimeSpan.FromHours(2));
        IClock clock = new SystemClock(new FixedTimeProvider(fixedTime));
        Assert.Equal(fixedTime.UtcDateTime, clock.UtcNow.UtcDateTime);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
