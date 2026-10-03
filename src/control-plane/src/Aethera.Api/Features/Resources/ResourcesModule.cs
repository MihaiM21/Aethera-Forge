// Owned by WP1.2 (Resource API). Only WP1.2 edits this file and the Aethera.Api/Features/Resources and Aethera.Infrastructure/Crypto folders.
// Program.cs already calls AddResources and MapResources; do not edit it. FluentValidation validators anywhere in Aethera.Api are registered
// automatically, so no registration is needed for them.
using Aethera.Api.Features.Resources.DomainNames;
using Aethera.Api.Features.Resources.Organizations;
using Aethera.Api.Features.Resources.Projects;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Features.Resources.Servers;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Domain;
using Aethera.Infrastructure;
using Aethera.Infrastructure.Crypto;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Features.Resources;

public static class ResourcesModule
{
    /// <summary>Registers resource services, ISecretProtector (AES-256-GCM envelope encryption) and the DNS resolver.</summary>
    public static IServiceCollection AddResources(this IServiceCollection services, IConfiguration configuration)
    {
        // The keyring is built on first use (host start-up forces it, see SecretProtectorStartupCheck), so it can see the hosting environment:
        // outside Development and Testing a missing master key stops the application from starting.
        services.TryAddSingleton(sp =>
        {
            var environment = sp.GetRequiredService<IHostEnvironment>();
            var mode = environment.IsDevelopment() ? KeyringMode.Development
                : environment.IsEnvironment("Testing") ? KeyringMode.Testing
                : KeyringMode.Strict;
            return MasterKeyring.Load(configuration, mode, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Aethera.Security"));
        });
        services.TryAddSingleton<ISecretProtector>(sp => new AesGcmSecretProtector(sp.GetRequiredService<MasterKeyring>()));
        services.AddHostedService<SecretProtectorStartupCheck>();

        services.TryAddScoped<SecretVault>();
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        return services;
    }

    /// <summary>Maps organizations, projects, environments, applications, services, env vars, secrets, volumes, domains, registries, servers.</summary>
    public static IEndpointRouteBuilder MapResources(this IEndpointRouteBuilder api)
    {
        OrganizationEndpoints.Map(api);
        ProjectEndpoints.Map(api);
        ServerEndpoints.Map(api);
        ApplicationEndpoints.Map(api);
        ServiceEndpoints.Map(api);
        EnvVarEndpoints.Map(api, "applications", "application");
        EnvVarEndpoints.Map(api, "services", "service");
        SecretEndpoints.Map(api);
        RegistryEndpoints.Map(api);
        VolumeEndpoints.Map(api);
        DomainEndpoints.Map(api);
        return api;
    }
}

/// <summary>Builds the secret protector when the host starts, so a missing or malformed master key is a clear start-up failure.</summary>
internal sealed class SecretProtectorStartupCheck(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The build-time OpenAPI generator (Microsoft.Extensions.ApiDescription.Server) starts the application without any configuration;
        // it never touches secrets, so it must not need a master key.
        if (AetheraHost.IsOpenApiGeneration) return Task.CompletedTask;

        _ = services.GetRequiredService<ISecretProtector>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
