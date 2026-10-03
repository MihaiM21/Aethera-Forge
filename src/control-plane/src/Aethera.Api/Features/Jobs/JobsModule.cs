// Owned by WP1.3 (Job system). Only WP1.3 edits this file and the Aethera.Api/Features/Jobs and Aethera.Infrastructure/Jobs folders.
// Program.cs already calls AddJobs, MapJobs and MapJobsHubs; do not edit it.
using System.Security.Cryptography;
using System.Text;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Infrastructure.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Prometheus;

namespace Aethera.Api.Features.Jobs;

public static class JobsModule
{
    /// <summary>Configuration key (or environment variable) of the optional bearer token that protects <c>/metrics</c>.</summary>
    public const string MetricsTokenKey = "AETHERA_METRICS_TOKEN";

    /// <summary>
    /// Registers the job system (<see cref="JobsServiceCollectionExtensions.AddAetheraJobSystem"/>: <c>IJobQueue</c>, the worker host,
    /// log pipeline, Redis or in-process live bus, metrics), SignalR and its relays, the <c>redis</c> readiness check and the HTTP metrics
    /// middleware.
    /// </summary>
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAetheraJobSystem(configuration);

        services.AddSignalR().AddJsonProtocol(options => JsonConventions.Configure(options.PayloadSerializerOptions));
        services.TryAddSingleton<LogStreamRelay>();
        services.AddHostedService<JobEventRelay>();

        services.AddSingleton<IReadinessCheck, RedisReadinessCheck>();

        // Program.cs is closed for editing, so the request-metrics middleware joins the pipeline through a startup filter.
        services.AddTransient<IStartupFilter, HttpMetricsStartupFilter>();
        return services;
    }

    /// <summary>Maps /jobs (list, get, cancel, retry, logs, and the optional echo endpoint) into the /api/v1 group.</summary>
    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder api) => api.MapJobEndpoints();

    /// <summary>
    /// Maps SignalR hubs at the application root under /hubs/* (the argument is the root route builder, not the /api/v1 group) and
    /// root-level /metrics. Hubs require at least the Viewer role (and the <c>read</c> scope for tokens); /metrics is protected by
    /// <c>AETHERA_METRICS_TOKEN</c> when that is set.
    /// </summary>
    public static IEndpointRouteBuilder MapJobsHubs(this IEndpointRouteBuilder root)
    {
        root.MapHub<JobsHub>("/hubs/jobs").RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Read);
        root.MapHub<LogsHub>("/hubs/logs").RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Read);
        root.MapMetricsEndpoint();
        return root;
    }

    /// <summary>
    /// <c>GET /metrics</c> in the Prometheus text format. <b>Open when <c>AETHERA_METRICS_TOKEN</c> is not set</b> (it reveals job and request
    /// counts, never payloads or secrets: put it behind your reverse proxy or set the token). When set, scrapers must send
    /// <c>Authorization: Bearer &lt;token&gt;</c>; anything else is 401.
    /// </summary>
    private static void MapMetricsEndpoint(this IEndpointRouteBuilder root)
    {
        root.MapGet("/metrics", IResult (HttpContext http, AetheraMetrics metrics, IConfiguration configuration) =>
            {
                var expected = configuration[MetricsTokenKey] ?? configuration["Aethera:Metrics:Token"];
                if (!string.IsNullOrEmpty(expected))
                {
                    var header = http.Request.Headers.Authorization.ToString();
                    var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : "";
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
                        return ApiProblems.Unauthenticated();
                }

                return Results.Stream(
                    async stream => await metrics.Registry.CollectAndExportAsTextAsync(stream, http.RequestAborted),
                    "text/plain; version=0.0.4; charset=utf-8");
            })
            .WithName("getMetrics")
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}

/// <summary><c>redis</c> in <c>GET /ready</c>. Not configured counts as ready: the in-process fan-out needs nothing.</summary>
internal sealed class RedisReadinessCheck : IReadinessCheck
{
    public string Name => "redis";

    public async Task<bool> IsReadyAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<ILiveBus>() is not RedisLiveBus redis || await redis.PingAsync(cancellationToken);
}

/// <summary>Adds the Prometheus HTTP request middleware (request count, duration, in progress) first in the pipeline.</summary>
internal sealed class HttpMetricsStartupFilter(AetheraMetrics metrics) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseHttpMetrics(options =>
        {
            options.InProgress.MetricFactory = metrics.Factory;
            options.RequestCount.MetricFactory = metrics.Factory;
            options.RequestDuration.MetricFactory = metrics.Factory;
        });
        next(app);
    };
}
