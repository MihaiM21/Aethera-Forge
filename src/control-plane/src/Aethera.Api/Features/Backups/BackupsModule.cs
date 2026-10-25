using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Features.Backups;

/// <summary>Backup destinations, policies, runs and restores (R1.1, ADR 0010).</summary>
public static class BackupsModule
{
    public static IServiceCollection AddBackups(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBackupInfrastructure(configuration);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, BackupRestoreJobHandler>());
        return services;
    }

    public static IEndpointRouteBuilder MapBackups(this IEndpointRouteBuilder api)
    {
        BackupDestinationEndpoints.Map(api);
        BackupPolicyEndpoints.Map(api);
        BackupRunEndpoints.Map(api);
        return api;
    }
}
