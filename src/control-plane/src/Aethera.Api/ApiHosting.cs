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
        services.AddAetheraForwardedHeaders();
        services.AddAetheraResponseCompression();
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

    /// <summary>
    /// The cross-cutting middleware, in order: forwarded headers (first, so everything after sees the real client address and scheme:
    /// login rate limit, audit, origin checks), request id, response compression, exception handling, status-code bodies, routing, CORS,
    /// authN, hub origin check, authZ.
    /// </summary>
    public static WebApplication UseAetheraApi(this WebApplication app)
    {
        app.UseAetheraForwardedHeaders();
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseResponseCompression();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRouting();
        app.UseMiddleware<BodySizeLimitMiddleware>();
        app.UseAetheraCors(app.Configuration);
        app.UseAuthentication();
        app.UseMiddleware<HubOriginMiddleware>(); // needs the principal; cookie-authenticated /hubs/* requests must come from our own origin
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

    /// <summary>Brotli and gzip for text responses (JSON, HTML, JS, CSS). Not over HTTPS terminated here (BREACH); Traefik compresses there.</summary>
    private static IServiceCollection AddAetheraResponseCompression(this IServiceCollection services) =>
        services.AddResponseCompression(options =>
        {
            options.MimeTypes = [.. Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes,
                "application/problem+json", "image/svg+xml", "font/woff2"];
        });

    private static byte[]? CursorKey(IConfiguration configuration) =>
        configuration["Aethera:Pagination:CursorKey"] is { Length: >= 16 } key ? System.Text.Encoding.UTF8.GetBytes(key) : null;
}
