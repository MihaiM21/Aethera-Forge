// Owned by WP1.3 (Job system). Only WP1.3 edits this file and the Aethera.Api/Features/Jobs and Aethera.Infrastructure/Jobs folders.
// Program.cs already calls AddJobs, MapJobs and MapJobsHubs; do not edit it.
namespace Aethera.Api.Features.Jobs;

public static class JobsModule
{
    /// <summary>Registers IJobQueue, the worker host (BackgroundService), Redis, SignalR and prometheus-net.</summary>
    public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
    {
        return services;
    }

    /// <summary>Maps /jobs (list, get, cancel, retry, logs) into the /api/v1 group.</summary>
    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder api)
    {
        return api;
    }

    /// <summary>
    /// Maps SignalR hubs at the application root under /hubs/* (the argument is the root route builder, not the /api/v1 group) and is the
    /// place for root-level /metrics. Root endpoints are NOT secure by default: hubs must call .RequireAuthorization() themselves.
    /// Register a Redis IReadinessCheck (Aethera.Api.Http) in AddJobs so /ready reports it.
    /// </summary>
    public static IEndpointRouteBuilder MapJobsHubs(this IEndpointRouteBuilder root)
    {
        return root;
    }
}
