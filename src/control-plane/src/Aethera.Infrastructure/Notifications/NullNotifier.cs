using Aethera.Domain;

namespace Aethera.Infrastructure.Notifications;

/// <summary>Drops every event. The default <see cref="INotifier"/> until the notifications module replaces it.</summary>
public sealed class NullNotifier : INotifier
{
    public Task PublishAsync(NotificationEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
