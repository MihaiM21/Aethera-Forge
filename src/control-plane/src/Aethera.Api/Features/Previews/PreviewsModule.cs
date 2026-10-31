using Aethera.Infrastructure.Previews;

namespace Aethera.Api.Features.Previews;

/// <summary>
/// Pull request preview deployments (R2.3, ADR 0012): previews of an application (<c>/applications/{id}/previews</c>), the
/// <c>preview.teardown</c> and <c>preview.sweep</c> jobs and the <see cref="Engine.Git.IPullRequestCommenter"/> implementations.
/// </summary>
public static class PreviewsModule
{
    public const string TeardownJobType = PreviewJobs.TeardownType;
    public const string SweepJobType = PreviewJobs.SweepType;

    public static IServiceCollection AddPreviews(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPreviewInfrastructure();
        return services;
    }

    public static IEndpointRouteBuilder MapPreviews(this IEndpointRouteBuilder api)
    {
        PreviewEndpoints.Map(api);
        return api;
    }
}
