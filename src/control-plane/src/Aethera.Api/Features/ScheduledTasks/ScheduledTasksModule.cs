using Aethera.Infrastructure.ScheduledTasks;

namespace Aethera.Api.Features.ScheduledTasks;

/// <summary>Scheduled tasks and their runs (R1.3, ADR 0010).</summary>
public static class ScheduledTasksModule
{
    /// <summary>Registers the scheduler, the <c>scheduled_task.run</c> job handler and the run log stream authorizer.</summary>
    public static IServiceCollection AddScheduledTasks(this IServiceCollection services, IConfiguration configuration) =>
        services.AddAetheraScheduledTasks(configuration);

    public static IEndpointRouteBuilder MapScheduledTasks(this IEndpointRouteBuilder api)
    {
        ScheduledTaskEndpoints.Map(api);
        return api;
    }
}
