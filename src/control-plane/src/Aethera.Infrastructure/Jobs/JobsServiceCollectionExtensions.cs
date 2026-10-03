using Aethera.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Jobs;

public static class JobsServiceCollectionExtensions
{
    public const string RedisConnectionStringName = "Redis";

    /// <summary>
    /// Registers the whole job system without any ASP.NET dependency, so it also runs in a plain generic host (a worker-only
    /// process, tests): queue, handlers registry, worker host, log pipeline, live bus (Redis when <c>ConnectionStrings:Redis</c> is set,
    /// otherwise in-process with a warning) and metrics. Requires <c>AddAetheraPersistence</c>. Add handlers with
    /// <c>services.AddSingleton&lt;IJobHandler, MyHandler&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddAetheraJobSystem(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobsOptions>().Configure<IHostEnvironment>((options, environment) =>
        {
            var section = configuration.GetSection(JobsOptions.Section);
            section.Bind(options);
            if (section["WorkerCount"] is not null) return; // an explicit setting always wins

            // ADR 0004 names AETHERA_JOB_CONCURRENCY.
            if (int.TryParse(configuration["AETHERA_JOB_CONCURRENCY"], out var concurrency))
                options.WorkerCount = concurrency;
            // Test hosts that did not ask for workers get none: a test that enqueues jobs it does not execute must not see them
            // claimed (and failed as unknown types) behind its back, and a test run must never touch a developer's real queue.
            else if (environment.IsEnvironment("Testing"))
                options.WorkerCount = 0;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ILiveBus>(sp =>
        {
            var redis = configuration.GetConnectionString(RedisConnectionStringName);
            if (!string.IsNullOrWhiteSpace(redis))
                return new RedisLiveBus(redis, sp.GetRequiredService<ILogger<RedisLiveBus>>());

            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Aethera.Jobs").LogWarning(
                "ConnectionStrings:Redis is not configured: live logs and job events are fanned out inside this process only "
                + "(fine for a single API instance, wrong for several). Postgres remains the source of truth.");
            return new InProcessLiveBus();
        });

        services.TryAddSingleton<AetheraMetrics>();
        services.TryAddSingleton<WorkerWakeSignal>();
        services.TryAddSingleton<JobEvents>();
        services.TryAddSingleton<JobStore>();
        services.TryAddSingleton<LogReader>();
        services.TryAddScoped<IJobQueue, PostgresJobQueue>();

        // Order matters: hosted services stop in reverse order, so the worker host (which flushes logs while stopping) stops
        // before the log ingestor drains its queue.
        services.TryAddSingleton<LogIngestor>();
        services.TryAddSingleton<ILogSinkFactory>(sp => sp.GetRequiredService<LogIngestor>());
        services.AddHostedService(sp => sp.GetRequiredService<LogIngestor>());
        services.TryAddSingleton<JobWorkerHost>();
        services.AddHostedService(sp => sp.GetRequiredService<JobWorkerHost>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, EchoJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILogStreamAuthorizer, JobLogStreamAuthorizer>());
        return services;
    }
}
