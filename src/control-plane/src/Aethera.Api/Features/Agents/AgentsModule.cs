// Owned by WP2.2 (Agent gateway). Only WP2.2 edits this file and the Aethera.Api/Features/Agents and Aethera.Infrastructure/Agents folders.
using Aethera.Api.Features.Agents.Gateway;
using Aethera.Infrastructure.Agents;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Agents;

public static class AgentsModule
{
    /// <summary>
    /// Registers the agent gateway: the infrastructure services (<see cref="AgentsServiceCollectionExtensions.AddAetheraAgents"/>), the gRPC
    /// services with the identity interceptor (8 MiB message limit, ADR 0002), the enrollment rate limiter and the Kestrel setup for the
    /// dedicated mTLS listener.
    /// </summary>
    public static IServiceCollection AddAgentGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAetheraAgents(configuration);

        services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = 8 * 1024 * 1024;
            options.MaxSendMessageSize = 8 * 1024 * 1024;
            options.Interceptors.Add<AgentAuthInterceptor>();
        });

        services.TryAddSingleton<EnrollRateLimiter>();
        services.TryAddSingleton<GatewayServerCertificate>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<KestrelServerOptions>, AgentGatewayKestrelSetup>());
        return services;
    }

    /// <summary>
    /// Keeps the two listeners apart (first in the pipeline): on the gateway port only gRPC calls to the agent services are served; on
    /// every other port those services do not exist, so a token can never travel over the plain-HTTP API listener.
    /// </summary>
    public static WebApplication UseAgentGateway(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<AgentGatewayOptions>>().Value;
        if (!options.Enabled) return app;
        var port = options.GrpcPort;
        app.Use(async (context, next) =>
        {
            var local = context.Connection.LocalPort;
            var isAgentCall = context.Request.Path.Value?.StartsWith("/aethera.agent.v1.", StringComparison.Ordinal) == true; // /aethera.agent.v1.AgentService/Connect
            if (local > 0 && ((local == port) != isAgentCall))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
        return app;
    }

    /// <summary>Maps <c>AgentService</c> and <c>EnrollmentService</c> (they are not part of the OpenAPI document).</summary>
    public static IEndpointRouteBuilder MapAgentGateway(this IEndpointRouteBuilder root)
    {
        root.MapGrpcService<AgentGrpcService>().ExcludeFromDescription();
        root.MapGrpcService<EnrollmentGrpcService>().ExcludeFromDescription();
        return root;
    }

    /// <summary>Maps the REST additions to <c>/servers</c> (join tokens, status, metrics, discovery, Docker inventory, maintenance) into the <c>/api/v1</c> group.</summary>
    public static IEndpointRouteBuilder MapAgents(this IEndpointRouteBuilder api) => AgentEndpoints.Map(api);
}
