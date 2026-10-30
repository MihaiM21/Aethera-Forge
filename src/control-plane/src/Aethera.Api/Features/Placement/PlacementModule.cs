namespace Aethera.Api.Features.Placement;

/// <summary>
/// Multi-server placement and resources (R2.1, ADR 0012): <c>/placement/suggest</c>, <c>/servers/capacity</c>, moving workloads between servers,
/// the <see cref="Domain.IPlacementService"/> implementation and build-elsewhere support.
/// </summary>
public static class PlacementModule
{
    public static IServiceCollection AddPlacement(this IServiceCollection services, IConfiguration configuration) => services;

    public static IEndpointRouteBuilder MapPlacement(this IEndpointRouteBuilder api) => api;
}
