using Aethera.Infrastructure.Mesh;

namespace Aethera.Api.Features.Mesh;

/// <summary>
/// The private mesh between servers (R2.2, ADR 0012): the <c>mesh</c> instance setting (<c>/system/mesh</c>), address allocation, the
/// <c>mesh.reconcile</c> job and per-server mesh status (<c>/servers/{id}/mesh</c>).
/// </summary>
public static class MeshModule
{
    /// <summary>Job type that pushes the desired mesh configuration to every connected server.</summary>
    public const string ReconcileJobType = MeshReconcileJobHandler.JobType;

    public static IServiceCollection AddMesh(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMeshInfrastructure(configuration);
        return services;
    }

    public static IEndpointRouteBuilder MapMesh(this IEndpointRouteBuilder api)
    {
        MeshEndpoints.Map(api);
        return api;
    }
}
