using Aethera.Infrastructure.Persistence;

namespace Aethera.Api.Http;

/// <summary>
/// A dependency checked by <c>GET /ready</c>. Register implementations (<c>services.AddSingleton&lt;IReadinessCheck, ...&gt;()</c>) from a
/// feature module; the response lists each by <see cref="Name"/>. PostgreSQL is built in; WP1.3 adds Redis.
/// </summary>
public interface IReadinessCheck
{
    /// <summary>Key in the <c>checks</c> object of the response (camelCase, e.g. <c>database</c>, <c>redis</c>).</summary>
    string Name { get; }

    /// <summary>True when the dependency is reachable. Exceptions count as "unavailable" and are logged, never returned.</summary>
    Task<bool> IsReadyAsync(IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class DatabaseReadinessCheck : IReadinessCheck
{
    public string Name => "database";

    public async Task<bool> IsReadyAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        // Resolved here so a missing/invalid connection string reports "unavailable" instead of a 500.
        var db = services.GetRequiredService<AetheraDbContext>();
        return await db.Database.CanConnectAsync(cancellationToken);
    }
}

public sealed record HealthResponse(string Status);

public sealed record ReadyResponse(string Status, IReadOnlyDictionary<string, string> Checks);

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
                foreach (var check in http.RequestServices.GetServices<IReadinessCheck>())
                {
                    var ok = false;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        ok = await check.IsReadyAsync(http.RequestServices, timeout.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // Never echo the exception (it can contain connection details); log it instead.
                        logger.LogWarning(ex, "Readiness check {Check} failed", check.Name);
                    }

                    results[check.Name] = ok ? "ok" : "unavailable";
                }

                var ready = results.Values.All(v => v == "ok");
                return Results.Json(new ReadyResponse(ready ? "ready" : "unavailable", results),
                    statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("GetReady").AllowAnonymous();

        return root;
    }
}
