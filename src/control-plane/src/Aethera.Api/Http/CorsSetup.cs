using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Http;

/// <summary>
/// CORS is off by default (the UI is served from the API's own origin). <c>AETHERA_CORS_ORIGINS</c> (comma-separated, exact origins such as
/// <c>https://ui.example.com</c>) enables a credentialed allowlist for custom frontends. <c>*</c> is ignored on purpose.
/// </summary>
public static class CorsSetup
{
    public const string PolicyName = "AetheraAllowList";
    public const string OriginsKey = "AETHERA_CORS_ORIGINS";

    public static IServiceCollection AddAetheraCors(this IServiceCollection services)
    {
        services.AddCors();
        // Resolved lazily so the final (test/host) configuration is used.
        services.AddOptions<CorsOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            var origins = ParseOrigins(configuration[OriginsKey]);
            if (origins.Length == 0) return;
            options.AddPolicy(PolicyName, policy => policy
                .WithOrigins(origins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials()
                .WithExposedHeaders("X-Request-Id", "ETag", "Location", "Retry-After", "Idempotency-Replayed")
                .SetPreflightMaxAge(TimeSpan.FromHours(1)));
        });
        return services;
    }

    public static string[] ParseOrigins(string? value) =>
        (value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(o => o != "*" && Uri.TryCreate(o, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https"))
            .Select(o => o.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsEnabled(IConfiguration configuration) => ParseOrigins(configuration[OriginsKey]).Length > 0;

    public static IApplicationBuilder UseAetheraCors(this IApplicationBuilder app, IConfiguration configuration) =>
        IsEnabled(configuration) ? app.UseCors(PolicyName) : app;
}
