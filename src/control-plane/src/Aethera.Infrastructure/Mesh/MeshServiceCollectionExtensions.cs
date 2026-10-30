using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Infrastructure.Mesh;

public static class MeshServiceCollectionExtensions
{
    /// <summary>
    /// The private mesh of the control plane (ADR 0012): the reconciler and its job, the trigger when an agent connects and the status monitor.
    /// Needs persistence, the job system and the agent transport.
    /// </summary>
    public static IServiceCollection AddMeshInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MeshOptions>(configuration.GetSection(MeshOptions.Section));
        services.TryAddScoped<MeshReconciler>();
        services.TryAddScoped<MeshReconcileScheduler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, MeshReconcileJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentConnectedHook, MeshConnectedHook>());
        services.TryAddSingleton<MeshMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<MeshMonitor>());
        return services;
    }
}
