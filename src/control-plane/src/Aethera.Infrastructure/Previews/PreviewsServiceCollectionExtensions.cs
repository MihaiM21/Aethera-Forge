using Aethera.Engine.Git;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Infrastructure.Previews;

public static class PreviewsServiceCollectionExtensions
{
    /// <summary>
    /// Pull request previews (ADR 0012): the service, the teardown and sweep jobs, the daily sweep scheduler and the GitHub and GitLab comment
    /// implementations. Needs persistence, the job system, the secret protector and the deployments.
    /// </summary>
    public static IServiceCollection AddPreviewInfrastructure(this IServiceCollection services)
    {
        services.TryAddScoped<PreviewService>();
        services.TryAddScoped<BackupSecretReader>();
        services.AddHttpClient(PullRequestCommenterBase.HttpClientName, client => client.Timeout = PullRequestCommenterBase.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPullRequestCommenter, GitHubPullRequestCommenter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPullRequestCommenter, GitLabPullRequestCommenter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, PreviewTeardownJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, PreviewSweepJobHandler>());
        services.TryAddSingleton<PreviewSweepScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<PreviewSweepScheduler>());
        return services;
    }
}
