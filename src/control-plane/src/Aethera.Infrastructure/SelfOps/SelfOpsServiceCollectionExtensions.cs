using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.SelfOps;

public static class SelfOpsServiceCollectionExtensions
{
    /// <summary>WP5.2: update checks, the updater channel and scheduled maintenance. Needs the agent gateway (<c>AgentSessionRegistry</c>) and the job queue.</summary>
    public static IServiceCollection AddSelfOps(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<UpdateOptions>(configuration.GetSection(UpdateOptions.Section));
        services.Configure<MaintenanceOptions>(configuration.GetSection(MaintenanceOptions.Section));

        services.TryAddSingleton(sp => new ReleaseChecker(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(20) },
            sp.GetRequiredService<IOptions<UpdateOptions>>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<ReleaseChecker>>()));
        services.TryAddSingleton<UpdaterControl>();
        services.TryAddSingleton<MaintenanceService>();
        services.TryAddScoped<LocalServerEnabler>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, AgentUpdateJobHandler>());

        services.AddHostedService<UpdateCheckService>();
        services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());
        services.AddHostedService<LocalServerAutoEnroller>();
        services.TryAddSingleton<LocalAgentBeacon>();
        services.AddHostedService(sp => sp.GetRequiredService<LocalAgentBeacon>());
        return services;
    }
}
