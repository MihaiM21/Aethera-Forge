using Aethera.Domain;
using Aethera.Infrastructure.Placement;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Features.Placement;

/// <summary>
/// Multi-server placement and resources (R2.1, ADR 0012): <c>/placement/suggest</c>, <c>/servers/capacity</c>, moving workloads between servers,
/// the <see cref="IPlacementService"/> implementation and build-elsewhere support (the pipeline part lives in the engine and
/// <c>Aethera.Infrastructure.Deployments</c>).
/// </summary>
public static class PlacementModule
{
    public static IServiceCollection AddPlacement(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<ServerCapacityReader>();
        services.TryAddScoped<IPlacementService, PlacementService>();
        return services;
    }

    public static IEndpointRouteBuilder MapPlacement(this IEndpointRouteBuilder api)
    {
        PlacementEndpoints.Map(api);
        MoveEndpoints.Map(api);
        return api;
    }
}
