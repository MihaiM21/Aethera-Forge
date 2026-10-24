using Aethera.Infrastructure.Dns;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.DnsProviders;

/// <summary>DNS providers (Cloudflare) and the DNS check of domains (R1.5, ADR 0010).</summary>
public static class DnsModule
{
    public static IServiceCollection AddDns(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CloudflareOptions>(configuration.GetSection(CloudflareOptions.Section));
        // One client for every provider (the token is passed per call). The base address is configurable for tests; 10 s per call.
        services.TryAddSingleton<ICloudflareClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CloudflareOptions>>().Value;
            var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                BaseAddress = new Uri(options.CloudflareApiBase.EndsWith('/') ? options.CloudflareApiBase : options.CloudflareApiBase + "/"),
                Timeout = TimeSpan.FromSeconds(10),
            };
            return new CloudflareClient(http);
        });
        services.TryAddScoped<DomainDnsChecker>();
        return services;
    }

    public static IEndpointRouteBuilder MapDns(this IEndpointRouteBuilder api)
    {
        DnsProviderEndpoints.Map(api);
        return api;
    }
}
