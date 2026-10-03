using System.Reflection;
using Aethera.Api.Features;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.OpenApi;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure;
using Aethera.Infrastructure.Auditing;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api;

/// <summary>
/// The shared plumbing every feature module relies on (WP1.0). <c>Program.cs</c> calls <see cref="AddAetheraApi"/>, the three feature
/// <c>AddXxx</c> methods, then <see cref="UseAetheraApi"/> and the <c>MapXxx</c> methods.
/// </summary>
public static class ApiHosting
{
    /// <summary>The prefix of the versioned API; every feature endpoint lives under it.</summary>
    public const string ApiBasePath = "/api/v1";

    public static IServiceCollection AddAetheraApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAetheraJson();
        services.AddAetheraProblemDetails();
        services.AddAetheraSecurity();
        services.AddAetheraCors();
        services.AddAetheraOpenApi();

        // Validators anywhere in this assembly are picked up; endpoints opt in with .Validate<T>().
        services.AddValidatorsFromAssembly(typeof(ApiHosting).Assembly, includeInternalTypes: true);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<IAuditLog, EfAuditLog>();
        services.AddSingleton(_ => new KeysetCursor(CursorKey(configuration)));
        services.AddSingleton<IReadinessCheck, DatabaseReadinessCheck>();
        return services;
    }

    /// <summary>The cross-cutting middleware, in order: request id, exception handling, status-code bodies, routing, CORS, authN, authZ.</summary>
    public static WebApplication UseAetheraApi(this WebApplication app)
    {
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRouting();
        app.UseMiddleware<BodySizeLimitMiddleware>();
        app.UseAetheraCors(app.Configuration);
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// Creates the <c>/api/v1</c> group. It requires an authenticated caller by default (anonymous endpoints must say
    /// <c>.AllowAnonymous()</c>) and limits request bodies to 1 MiB (override per endpoint with <c>RequestSizeLimit</c> metadata).
    /// </summary>
    public static RouteGroupBuilder MapApiGroup(this IEndpointRouteBuilder root)
    {
        var api = root.MapGroup(ApiBasePath);
        api.RequireAuthorization();
        api.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024 * 1024));
        return api;
    }

    /// <summary>Lets registered <see cref="IApiEndpointContributor"/>s add endpoints (used by tests).</summary>
    public static void MapContributors(this IEndpointRouteBuilder api)
    {
        foreach (var contributor in api.ServiceProvider.GetServices<IApiEndpointContributor>())
            contributor.MapEndpoints(api);
    }

    private static byte[]? CursorKey(IConfiguration configuration) =>
        configuration["Aethera:Pagination:CursorKey"] is { Length: >= 16 } key ? System.Text.Encoding.UTF8.GetBytes(key) : null;
}
