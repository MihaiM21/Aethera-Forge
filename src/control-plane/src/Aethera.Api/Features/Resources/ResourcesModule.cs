// Owned by WP1.2 (Resource API). Only WP1.2 edits this file and the Aethera.Api/Features/Resources and Aethera.Infrastructure/Crypto folders.
// Program.cs already calls AddResources and MapResources; do not edit it. FluentValidation validators anywhere in Aethera.Api are registered
// automatically, so no registration is needed for them.
namespace Aethera.Api.Features.Resources;

public static class ResourcesModule
{
    /// <summary>Registers resource services, ISecretProtector and repositories/queries.</summary>
    public static IServiceCollection AddResources(this IServiceCollection services, IConfiguration configuration)
    {
        return services;
    }

    /// <summary>Maps organizations, projects, environments, applications, services, env vars, secrets, volumes, domains, registries.</summary>
    public static IEndpointRouteBuilder MapResources(this IEndpointRouteBuilder api)
    {
        return api;
    }
}
