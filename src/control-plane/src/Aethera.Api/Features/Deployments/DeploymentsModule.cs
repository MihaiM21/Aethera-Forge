using Aethera.Infrastructure.Deployments;

namespace Aethera.Api.Features.Deployments;

public static class DeploymentsModule
{
    /// <summary>Registers the deployment engine services (strategies, proxy provider, deploy and lifecycle job handlers).</summary>
    public static IServiceCollection AddDeployments(this IServiceCollection services, IConfiguration configuration) =>
        services.AddAetheraDeployments(configuration);

    /// <summary>Maps deployments, lifecycle actions, rollback, build detection, proxy and webhook management into <c>/api/v1</c>.</summary>
    public static IEndpointRouteBuilder MapDeployments(this IEndpointRouteBuilder api)
    {
        DeploymentEndpoints.Map(api);
        DeploymentLogEndpoints.Map(api);
        WebhookEndpoints.MapManagement(api);
        BuildEndpoints.Map(api);
        GitCredentialEndpoints.Map(api);
        return api;
    }

    /// <summary>Maps the public Git webhook receiver at the root (<c>POST /webhooks/git/{endpointId}</c>).</summary>
    public static IEndpointRouteBuilder MapGitWebhooks(this IEndpointRouteBuilder app)
    {
        WebhookEndpoints.MapReceiver(app);
        return app;
    }
}
