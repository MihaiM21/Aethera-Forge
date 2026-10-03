using System.Threading.RateLimiting;
using Aethera.Api.Http.Errors;
using Aethera.Infrastructure.Auth;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Auth;

/// <summary>
/// Per-client-IP fixed-window limit for credential endpoints (10 attempts per minute by default,
/// <c>Aethera:Auth:LoginRateLimitPermits</c> / <c>LoginRateLimitWindow</c>), built on <see cref="PartitionedRateLimiter"/> from
/// <c>System.Threading.RateLimiting</c>, the engine behind ASP.NET Core's rate-limiting middleware. It is applied as an endpoint filter
/// rather than with <c>app.UseRateLimiter()</c> because the pipeline in <c>Program.cs</c> is fixed and has no rate-limiter middleware;
/// as a bonus the 429 is a normal ADR 0003 problem body with the request id. This is the per-IP layer; the per-account layer is the
/// lockout on the user row (<c>User.RecordFailedLogin</c>).
/// </summary>
public sealed class LoginRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public LoginRateLimiter(IOptionsMonitor<AuthOptions> options)
    {
        var settings = options.CurrentValue;
        _limiter = PartitionedRateLimiter.Create<string, string>(ip =>
            RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, settings.LoginRateLimitPermits),
                Window = settings.LoginRateLimitWindow > TimeSpan.Zero ? settings.LoginRateLimitWindow : TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    /// <summary>Takes one permit for <paramref name="clientIp"/>. Returns null when allowed, otherwise the 429 problem.</summary>
    public ApiProblem? TryAcquire(string? clientIp)
    {
        using var lease = _limiter.AttemptAcquire(clientIp ?? "unknown");
        if (lease.IsAcquired) return null;

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? (int)Math.Ceiling(value.TotalSeconds) : 60;
        return ApiProblems.RateLimited(Math.Max(1, retryAfter));
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>Endpoint filter: spends one <see cref="LoginRateLimiter"/> permit per request, answering <c>429 rate_limited</c> when the IP is out.</summary>
public sealed class LoginRateLimitFilter(LoginRateLimiter limiter) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        limiter.TryAcquire(context.HttpContext.Connection.RemoteIpAddress?.ToString()) is { } rejected
            ? ValueTask.FromResult<object?>(rejected)
            : next(context);
}
