using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Notifications.Detectors;
using Aethera.Infrastructure.SelfOps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aethera.Infrastructure.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the notifier (replacing the no-op one), email and webhook delivery, the <c>notification.send</c> job handler and the detectors
    /// (ADR 0010). Needs the job system and persistence.
    /// </summary>
    public static IServiceCollection AddAetheraNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NotificationOptions>().Configure<IHostEnvironment>((options, environment) =>
        {
            var section = configuration.GetSection(NotificationOptions.Section);
            section.Bind(options);
            options.PublicUrl = Normalize(options.PublicUrl ?? configuration[NotificationOptions.PublicUrlKey] ?? DomainUrl(configuration["AETHERA_DOMAIN"]));
            // Tests call the detectors themselves; a test host must not scan its database in the background.
            if (section["DetectorsEnabled"] is null && environment.IsEnvironment("Testing")) options.DetectorsEnabled = false;
        });

        services.TryAddSingleton<Notifier>();
        services.Replace(ServiceDescriptor.Singleton<INotifier>(sp => sp.GetRequiredService<Notifier>()));

        services.TryAddSingleton<SmtpEmailSender>();
        services.TryAddSingleton(_ => new ResendEmailSender(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        }));
        services.TryAddSingleton<IEmailSender, RoutingEmailSender>();
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>();
            return new WebhookSender(WebhookSender.CreateClient(options.Value), options);
        });
        services.TryAddScoped<NotificationSecretReader>();
        services.TryAddScoped<NotificationDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, NotificationSendJobHandler>());

        services.TryAddSingleton<ICertificateProbe, TlsCertificateProbe>();
        services.TryAddSingleton<ServerHealthDetector>();
        services.TryAddSingleton<ResourceUsageDetector>();
        services.TryAddSingleton<ContainerDetector>();
        services.TryAddSingleton<CertificateDetector>();
        services.TryAddSingleton(sp => new UpdateDetector(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<Notifier>(), sp.GetService<ReleaseChecker>()));
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<ServerHealthDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<ResourceUsageDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<ContainerDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<CertificateDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<UpdateDetector>());
        services.TryAddSingleton<MonitoringDetectorService>();
        services.AddHostedService(sp => sp.GetRequiredService<MonitoringDetectorService>());
        return services;
    }

    private static string? DomainUrl(string? domain) => string.IsNullOrWhiteSpace(domain) ? null : "https://" + domain.Trim();

    private static string? Normalize(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
}
