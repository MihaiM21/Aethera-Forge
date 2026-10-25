using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Infrastructure.ScheduledTasks;

public static class ScheduledTasksServiceCollectionExtensions
{
    /// <summary>Registers the scheduler, the <c>scheduled_task.run</c> job handler, the producer service and the log stream authorizer of task runs.</summary>
    public static IServiceCollection AddAetheraScheduledTasks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ScheduledTaskOptions>().Bind(configuration.GetSection(ScheduledTaskOptions.Section));
        services.TryAddScoped<ScheduledTaskService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, ScheduledTaskRunJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILogStreamAuthorizer, ScheduledTaskLogStreamAuthorizer>());
        services.AddSingleton<ScheduledTaskScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<ScheduledTaskScheduler>());
        return services;
    }
}
