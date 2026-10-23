using Aethera.Domain;
using Aethera.Infrastructure.Notifications;

namespace Aethera.Api.Features.Notifications;

/// <summary>Email settings, notification channels, alerts (R1.2, ADR 0010).</summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<INotifier, NullNotifier>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder api) => api;
}
