namespace Aethera.Api.Features.Backups;

/// <summary>Backup destinations, policies, runs and restores (R1.1, ADR 0010).</summary>
public static class BackupsModule
{
    public static IServiceCollection AddBackups(this IServiceCollection services, IConfiguration configuration) => services;

    public static IEndpointRouteBuilder MapBackups(this IEndpointRouteBuilder api) => api;
}
