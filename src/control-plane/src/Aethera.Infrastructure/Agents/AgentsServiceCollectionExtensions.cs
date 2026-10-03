using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Monitoring;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Protocol;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Agents.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aethera.Infrastructure.Agents;

public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent gateway without any ASP.NET dependency (WP2.2): internal CA, enrollment, session registry and stream handler,
    /// ingestion (metrics, logs, events, discovery), command dispatch with <see cref="AgentTransport"/>, the transport resolver, the status
    /// service and the background services (CA/revocation loading, reachability probe, metrics retention).
    /// </summary>
    /// <remarks>
    /// Requires <c>AddAetheraPersistence</c>, the job system (<c>ILiveBus</c> for log fan-out) and an <c>ISecretProtector</c> (the master key
    /// envelope). Under the <c>Testing</c> environment the gateway is off unless <c>Aethera:Agents:Enabled</c> says otherwise, so ordinary API
    /// tests start no listener, probe or retention loop.
    /// </remarks>
    public static IServiceCollection AddAetheraAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentGatewayOptions>().Configure<IHostEnvironment>((options, environment) =>
        {
            var section = configuration.GetSection(AgentGatewayOptions.Section);
            section.Bind(options);
            if (section["Enabled"] is null && environment.IsEnvironment("Testing")) options.Enabled = false;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton(CommandMapper.Default);

        services.TryAddSingleton<IInternalCa, InternalCa>();
        services.TryAddSingleton<AgentSessionRegistry>();
        services.TryAddSingleton<AgentAudit>();
        services.TryAddSingleton<ServerStatusService>();
        services.TryAddSingleton<AgentEnrollmentService>();
        services.TryAddSingleton<AgentLogIngestor>();
        services.TryAddSingleton<AgentMetricsIngestor>();
        services.TryAddSingleton<AgentEventIngestor>();
        services.TryAddSingleton<AgentDiscoveryStore>();
        services.TryAddSingleton<AgentCommandDispatcher>();
        services.TryAddSingleton<AgentSessionHandler>();
        services.TryAddSingleton<MetricsRetentionService>();
        services.TryAddSingleton<IReachabilityProbe, TcpReachabilityProbe>();
        services.TryAddSingleton<ISshFallbackPolicy, DbSshFallbackPolicy>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServerTransport, AgentTransport>());
        services.TryAddSingleton<IServerTransportResolver, ServerTransportResolver>();

        services.AddHostedService<AgentGatewayLifetimeService>();
        services.AddHostedService(sp => sp.GetRequiredService<MetricsRetentionService>());
        services.AddHostedService<ReachabilityProbeService>();
        return services;
    }
}
