using Aethera.Infrastructure.Notifications;

namespace Aethera.Api.Features.Notifications;

/// <summary>Email settings, notification channels, alerts (R1.2, ADR 0010).</summary>
public static class NotificationsModule
{
    /// <summary>Registers the notifier (replacing the no-op one), delivery, the <c>notification.send</c> job and the detectors.</summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration) =>
        services.AddAetheraNotifications(configuration);

    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder api)
    {
        EmailEndpoints.Map(api);
        ChannelEndpoints.Map(api);
        AlertEndpoints.Map(api);
        return api;
    }
}
