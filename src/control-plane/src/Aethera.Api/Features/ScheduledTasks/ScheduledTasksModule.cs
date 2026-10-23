namespace Aethera.Api.Features.ScheduledTasks;

/// <summary>Scheduled tasks and their runs (R1.3, ADR 0010).</summary>
public static class ScheduledTasksModule
{
    public static IServiceCollection AddScheduledTasks(this IServiceCollection services, IConfiguration configuration) => services;

    public static IEndpointRouteBuilder MapScheduledTasks(this IEndpointRouteBuilder api) => api;
}
