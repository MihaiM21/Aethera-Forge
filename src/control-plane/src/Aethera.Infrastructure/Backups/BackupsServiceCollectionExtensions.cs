using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Infrastructure.Backups;

public static class BackupsServiceCollectionExtensions
{
    /// <summary>
    /// The backup machinery of the control plane (ADR 0010): the run job, the scheduler, the S3 store and the dump of Aethera's own database. Needs
    /// persistence, the job system, the master keyring, <c>ISecretProtector</c> and <c>INotifier</c>. The restore job is registered by the API module.
    /// </summary>
    public static IServiceCollection AddBackupInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.Section));
        services.TryAddSingleton<IBackupObjectStore, S3BackupObjectStore>();
        services.TryAddSingleton<IControlPlaneDump, PgDumpControlPlaneDump>();
        services.TryAddScoped<BackupSecretReader>();
        services.TryAddScoped<WorkloadContainers>();
        services.TryAddScoped<BackupRunner>();
        services.TryAddScoped<BackupJobService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, BackupRunJobHandler>());
        services.TryAddSingleton<BackupScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupScheduler>());
        return services;
    }
}
