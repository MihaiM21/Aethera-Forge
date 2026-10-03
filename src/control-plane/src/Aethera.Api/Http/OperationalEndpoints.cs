using Aethera.Infrastructure.Persistence;

namespace Aethera.Api.Http;

/// <summary>The outcome of one readiness check, as listed in the <c>checks</c> object of <c>GET /ready</c> (lower case).</summary>
public enum ReadinessStatus
{
    /// <summary>The dependency was reached.</summary>
    Ok,

    /// <summary>The dependency is configured (or required) and cannot be used: the instance is not ready (503).</summary>
    Unavailable,

    /// <summary>The dependency is not configured and nothing needs it: reported, but never fails readiness.</summary>
    Skipped,
}

/// <summary>
/// A check outcome with an optional human-readable <paramref name="Detail"/>. The detail is returned to anonymous callers, so it must
/// be a fixed phrase written by the check (for example "Redis is not configured"): <b>never</b> an exception message, host name,
/// port, user name or any part of a connection string.
/// </summary>
public readonly record struct ReadinessResult(ReadinessStatus Status, string? Detail = null)
{
    public static ReadinessResult Ok(string? detail = null) => new(ReadinessStatus.Ok, detail);

    public static ReadinessResult Unavailable(string? detail = null) => new(ReadinessStatus.Unavailable, detail);

    public static ReadinessResult Skipped(string? detail = null) => new(ReadinessStatus.Skipped, detail);

    public static ReadinessResult FromBoolean(bool ok) => ok ? Ok() : Unavailable();
}

/// <summary>
/// A dependency checked by <c>GET /ready</c>. Register implementations (<c>services.AddSingleton&lt;IReadinessCheck, ...&gt;()</c>) from a
/// feature module; the response lists each by <see cref="Name"/>. PostgreSQL is built in; WP1.3 adds Redis.
/// </summary>
public interface IReadinessCheck
{
    /// <summary>Key in the <c>checks</c> object of the response (camelCase, e.g. <c>database</c>, <c>redis</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Probes the dependency. Return <see cref="ReadinessResult.Skipped"/> when it is not configured. An exception counts as
    /// <see cref="ReadinessStatus.Unavailable"/> and is logged, never returned.
    /// </summary>
    Task<ReadinessResult> CheckAsync(IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class DatabaseReadinessCheck : IReadinessCheck
{
    public string Name => "database";

    public async Task<ReadinessResult> CheckAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        // Resolved here so a missing/invalid connection string reports "unavailable" instead of a 500.
        var db = services.GetRequiredService<AetheraDbContext>();
        return ReadinessResult.FromBoolean(await db.Database.CanConnectAsync(cancellationToken));
    }
}

public sealed record HealthResponse(string Status);

/// <param name="Status"><c>ready</c>, or <c>unavailable</c> when any check is unavailable (HTTP 503).</param>
/// <param name="Checks">Check name to <c>ok</c> / <c>unavailable</c> / <c>skipped</c>.</param>
/// <param name="Details">Check name to its detail phrase, for the checks that gave one.</param>
public sealed record ReadyResponse(string Status, IReadOnlyDictionary<string, string> Checks, IReadOnlyDictionary<string, string> Details);

public static class OperationalEndpoints
{
    /// <summary>Maps the unversioned operational endpoints <c>/health</c> (liveness) and <c>/ready</c> (readiness).</summary>
    public static IEndpointRouteBuilder MapOperationalEndpoints(this IEndpointRouteBuilder root)
    {
        root.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
            .WithName("GetHealth").AllowAnonymous();

        root.MapGet("/ready", async (HttpContext http, ILoggerFactory loggers, CancellationToken cancellationToken) =>
            {
                var logger = loggers.CreateLogger("Aethera.Readiness");
                var results = new Dictionary<string, string>();
                var details = new Dictionary<string, string>();
                foreach (var check in http.RequestServices.GetServices<IReadinessCheck>())
                {
                    var result = ReadinessResult.Unavailable();
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        result = await check.CheckAsync(http.RequestServices, timeout.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // Never echo the exception (it can contain connection details); log it instead.
                        logger.LogWarning(ex, "Readiness check {Check} failed", check.Name);
                    }

                    results[check.Name] = result.Status switch
                    {
                        ReadinessStatus.Ok => "ok",
                        ReadinessStatus.Skipped => "skipped",
                        _ => "unavailable",
                    };
                    if (!string.IsNullOrWhiteSpace(result.Detail)) details[check.Name] = result.Detail;
                }

                // Only an unavailable dependency makes the instance not ready; a skipped one (not configured) never does.
                var ready = !results.Values.Contains("unavailable");
                return Results.Json(new ReadyResponse(ready ? "ready" : "unavailable", results, details),
                    statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("GetReady").AllowAnonymous();

        return root;
    }
}
