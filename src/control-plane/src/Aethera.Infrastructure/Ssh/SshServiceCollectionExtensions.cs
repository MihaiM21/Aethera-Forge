using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Ssh.Bootstrap;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aethera.Infrastructure.Ssh;

/// <summary>
/// The real <see cref="ISshFallbackPolicy"/> (ADR 0002): the SSH transport may stand in for a missing agent session when the server has an
/// SSH credential, is not disabled, and its <c>allowSshFallback</c> switch (default on) is not turned off.
/// </summary>
public sealed class SshFallbackPolicy(IServiceScopeFactory scopes, SshSettingsStore settings) : ISshFallbackPolicy
{
    public async ValueTask<bool> AllowsFallbackAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var usable = await db.Servers.AsNoTracking().AnyAsync(s => s.Id == serverId && s.SshCredentialSecretId != null && s.Lifecycle != ServerLifecycle.Disabled, cancellationToken);
            if (!usable) return false;
        }

        return await settings.FallbackAllowedAsync(serverId, cancellationToken);
    }
}

public static class SshServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SSH transport next to <see cref="AgentTransport"/> (WP2.3): connection pool with TOFU host keys, credential access over the
    /// secret machinery, the Docker CLI transport, the metrics poller, the bootstrap service and its job. Called from
    /// <c>AddAetheraAgents</c> before the default fallback policy is registered, so <see cref="SshFallbackPolicy"/> wins.
    /// </summary>
    public static IServiceCollection AddAetheraSsh(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SshOptions>().Configure<IHostEnvironment>((options, environment) =>
        {
            var section = configuration.GetSection(SshOptions.Section);
            section.Bind(options);
            if (section["Enabled"] is null && environment.IsEnvironment("Testing")) options.Enabled = false;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ISshConnector, SshNetConnector>();
        services.TryAddSingleton<SshSettingsStore>();
        services.TryAddSingleton<SshAccessProvider>();
        services.TryAddSingleton<SshHostKeyService>();
        services.TryAddSingleton<SshConnectionPool>();
        services.TryAddSingleton<SshHealthProber>();
        services.TryAddSingleton<ISshFallbackPolicy, SshFallbackPolicy>();
        services.TryAddSingleton<IAgentSessionProbe, RegistryAgentSessionProbe>();
        services.TryAddSingleton<SshBootstrapService>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServerTransport, SshTransport>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, ServerInstallAgentJobHandler>());
        services.AddSingleton<SshMetricsPoller>();
        services.AddHostedService(sp => sp.GetRequiredService<SshMetricsPoller>());
        return services;
    }
}
