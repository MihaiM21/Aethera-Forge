using Aethera.Engine.Deployments;
using Aethera.Engine.Proxy;
using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Infrastructure.Deployments;

public static class DeploymentsServiceCollectionExtensions
{
    /// <summary>Registers the deployment strategies, the proxy provider, the deploy and lifecycle job handlers and <see cref="DeploymentService"/>.</summary>
    public static IServiceCollection AddAetheraDeployments(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProxyOptions>().Bind(configuration.GetSection(ProxyOptions.SectionName));
        services.TryAddSingleton<IProxyProvider, TraefikProxyProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentStrategy, RecreateStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentStrategy, LowDowntimeStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentStrategy, ComposeStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, DeployJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, LifecycleJobHandler>());
        services.TryAddScoped<DeploymentService>();
        return services;
    }
}
