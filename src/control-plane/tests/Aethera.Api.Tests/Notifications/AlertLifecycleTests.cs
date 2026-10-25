using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Notifications;

namespace Aethera.Api.Tests.Notifications;

/// <summary>The alert lifecycle of <see cref="Notifier"/>: open, refresh, resolve, reminders, one open alert per key, channel selection.</summary>
[Collection(NotificationsCollection.Name)]
public sealed class AlertLifecycleTests(NotificationsFixture fixture)
{
    private static NotificationEvent Failure(Guid org, string key, string type = NotificationEvents.ServerUnreachable, AlertSeverity severity = AlertSeverity.Critical) =>
        new(org, type, severity, "Server web-1 is unreachable", "No agent session.") { ResourceType = "server", ResourceId = Guid.NewGuid(), AlertKey = key };

    private static NotificationEvent Recovery(Guid org, string key, string type = NotificationEvents.ServerRecovered) =>
        new(org, type, AlertSeverity.Info, "Server web-1 is back") { ResourceType = "server", AlertKey = key, Resolves = true };

    [RequiresDatabaseFact]
    public async Task Open_StoresAlert_AndQueuesOneJobPerSubscribedChannel()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org);
        await fixture.Factory.AddChannelAsync(org, kind: NotificationChannelKind.Webhook);

        await fixture.Notifier.PublishAsync(Failure(org, "server-down:a"), default);

        var alert = Assert.Single(await fixture.Factory.AlertsAsync(org));
        Assert.Equal(AlertState.Open, alert.State);
        Assert.Equal(NotificationEvents.ServerUnreachable, alert.Type);
        Assert.Equal(1, alert.NotificationCount);
        Assert.NotNull(alert.LastNotifiedAt);
        var sends = await fixture.Factory.SendsAsync(org);
        Assert.Equal(2, sends.Count);
        Assert.All(sends, s => { Assert.Equal(NotificationKinds.Opened, s.Kind); Assert.Equal(alert.Id, s.AlertId); });
        Assert.Equal(2, sends.Select(s => s.ChannelId).Distinct().Count());
    }

    [RequiresDatabaseFact]
    public async Task Refresh_UpdatesLastSeen_WithoutNotifyingAgain()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org);

        await fixture.Notifier.PublishAsync(Failure(org, "k1"), default);
        var opened = Assert.Single(await fixture.Factory.AlertsAsync(org));

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.Notifier.PublishAsync(Failure(org, "k1"), default);

        var refreshed = Assert.Single(await fixture.Factory.AlertsAsync(org));
        Assert.True(refreshed.LastSeenAt > opened.LastSeenAt);
        Assert.Equal(opened.OpenedAt, refreshed.OpenedAt);
        Assert.Single(await fixture.Factory.SendsAsync(org));
    }

    [RequiresDatabaseFact]
    public async Task Reminder_GoesOutEverySixHours_WhileOpen()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org);

        await fixture.Notifier.PublishAsync(Failure(org, "k2"), default);
        fixture.Clock.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59));
        await fixture.Notifier.PublishAsync(Failure(org, "k2"), default);
        Assert.Single(await fixture.Factory.SendsAsync(org));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Notifier.PublishAsync(Failure(org, "k2"), default);
        var sends = await fixture.Factory.SendsAsync(org);
        Assert.Equal(2, sends.Count);
        Assert.Equal(NotificationKinds.Reminder, sends[1].Kind);

        // The next reminder is six hours after the last one, not after the open.
        fixture.Clock.Advance(TimeSpan.FromHours(5));
        await fixture.Notifier.PublishAsync(Failure(org, "k2"), default);
        Assert.Equal(2, (await fixture.Factory.SendsAsync(org)).Count);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.Notifier.PublishAsync(Failure(org, "k2"), default);
        Assert.Equal(3, (await fixture.Factory.SendsAsync(org)).Count);
        Assert.Equal(3, (await fixture.Factory.AlertsAsync(org)).Single().NotificationCount);
    }

    [RequiresDatabaseFact]
    public async Task InfoAlerts_NeverRemind()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org, [NotificationEvents.UpdateAvailable]);

        var update = new NotificationEvent(org, NotificationEvents.UpdateAvailable, AlertSeverity.Info, "Aethera 9.9.9 is available") { AlertKey = "update:9.9.9" };
        await fixture.Notifier.PublishAsync(update, default);
        fixture.Clock.Advance(TimeSpan.FromHours(30));
        await fixture.Notifier.PublishAsync(update, default);

        Assert.Single(await fixture.Factory.SendsAsync(org));
    }

    [RequiresDatabaseFact]
    public async Task Resolve_SendsRecovered_OnlyWhenAnAlertWasOpen_AndTheNextFailureOpensANewAlert()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org);

        await fixture.Notifier.PublishAsync(Recovery(org, "k3"), default); // nothing open: silent
        Assert.Empty(await fixture.Factory.SendsAsync(org));
        Assert.Empty(await fixture.Factory.AlertsAsync(org));

        await fixture.Notifier.PublishAsync(Failure(org, "k3"), default);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        await fixture.Notifier.PublishAsync(Recovery(org, "k3"), default);

        var alert = Assert.Single(await fixture.Factory.AlertsAsync(org));
        Assert.Equal(AlertState.Resolved, alert.State);
        Assert.NotNull(alert.ResolvedAt);
        var sends = await fixture.Factory.SendsAsync(org);
        Assert.Equal(2, sends.Count);
        Assert.Equal(NotificationKinds.Resolved, sends[1].Kind);
        Assert.Equal(NotificationEvents.ServerRecovered, sends[1].Type);
        Assert.Contains("is back", NotificationMessageBuilder.SubjectOf(sends[1]) + sends[1].Title);
        Assert.EndsWith("resolved", NotificationMessageBuilder.SubjectOf(sends[1]));

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Notifier.PublishAsync(Failure(org, "k3"), default);
        var alerts = await fixture.Factory.AlertsAsync(org);
        Assert.Equal(2, alerts.Count);
        Assert.Single(alerts, a => a.State == AlertState.Open);
        Assert.Equal(3, (await fixture.Factory.SendsAsync(org)).Count);
    }

    [RequiresDatabaseFact]
    public async Task ConcurrentPublishes_LeaveOneOpenAlert()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Notifier.PublishAsync(Failure(org, "race"), default)));

        var alert = Assert.Single(await fixture.Factory.AlertsAsync(org));
        Assert.Equal(AlertState.Open, alert.State);
        Assert.True((await fixture.Factory.SendsAsync(org)).Count >= 1);
    }

    [RequiresDatabaseFact]
    public async Task SuccessEvent_WithoutAnOpenAlert_IsDeliveredAsAPlainEvent()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org, [NotificationEvents.DeploymentSucceeded]);
        await fixture.Factory.AddChannelAsync(org, [NotificationEvents.DeploymentFailed]);

        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.DeploymentSucceeded, AlertSeverity.Info, "Deployment #3 of api succeeded")
            { AlertKey = "deploy:x", Resolves = true }, default);

        var send = Assert.Single(await fixture.Factory.SendsAsync(org));
        Assert.Equal(NotificationKinds.Event, send.Kind);
        Assert.Empty(await fixture.Factory.AlertsAsync(org));
    }

    [RequiresDatabaseFact]
    public async Task Recovery_AlsoReachesChannelsSubscribedOnlyToTheOpeningEvent()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        var failedOnly = await fixture.Factory.AddChannelAsync(org, [NotificationEvents.DeploymentFailed]);

        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.DeploymentFailed, AlertSeverity.Critical, "Deployment #1 failed") { AlertKey = "deploy:w" }, default);
        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.DeploymentSucceeded, AlertSeverity.Info, "Deployment #2 succeeded") { AlertKey = "deploy:w", Resolves = true }, default);

        var sends = await fixture.Factory.SendsAsync(org);
        Assert.Equal([NotificationKinds.Opened, NotificationKinds.Resolved], sends.Select(s => s.Kind));
        Assert.All(sends, s => Assert.Equal(failedOnly.Id, s.ChannelId));
    }

    [RequiresDatabaseFact]
    public async Task WithoutSubscribers_TheAlertIsStored_AndAnnouncedOnceSomeoneSubscribes()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;

        await fixture.Notifier.PublishAsync(Failure(org, "late"), default);
        Assert.Single(await fixture.Factory.AlertsAsync(org));
        Assert.Empty(await fixture.Factory.SendsAsync(org));

        await fixture.Factory.AddChannelAsync(org);
        await fixture.Notifier.PublishAsync(Failure(org, "late"), default);
        var send = Assert.Single(await fixture.Factory.SendsAsync(org));
        Assert.Equal(NotificationKinds.Opened, send.Kind);
    }

    [RequiresDatabaseFact]
    public async Task EventWithoutAlertKey_IsDeliveredButNotStored()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org, [NotificationEvents.BackupFailed]);

        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.BackupFailed, AlertSeverity.Warning, "Backup of db failed"), default);
        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.BackupFailed, AlertSeverity.Warning, "Backup of db failed"), default);

        Assert.Empty(await fixture.Factory.AlertsAsync(org));
        Assert.Equal(2, (await fixture.Factory.SendsAsync(org)).Count);
    }

    [RequiresDatabaseFact]
    public async Task Publish_NeverThrows_EvenWhenTheOrganizationDoesNotExist()
    {
        await fixture.Notifier.PublishAsync(Failure(Guid.NewGuid(), "ghost"), default);
        await fixture.Notifier.PublishAsync(Failure(Guid.NewGuid(), "ghost"), new CancellationToken(canceled: true));
    }

    // ---- channel selection ------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Selection_SkipsDisabledChannels_UnsubscribedEvents_AndOtherOrganizations()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        var wanted = await fixture.Factory.AddChannelAsync(org, [NotificationEvents.ServerUnreachable]);
        await fixture.Factory.AddChannelAsync(org, [NotificationEvents.ServerUnreachable], enabled: false);
        await fixture.Factory.AddChannelAsync(org, [NotificationEvents.BackupFailed]);
        await fixture.Factory.AddChannelAsync(other.Identity.OrganizationId, [NotificationEvents.ServerUnreachable]);

        await fixture.Notifier.PublishAsync(Failure(org, "sel"), default);

        var send = Assert.Single(await fixture.Factory.SendsAsync(org));
        Assert.Equal(wanted.Id, send.ChannelId);
        Assert.Empty(await fixture.Factory.SendsAsync(other.Identity.OrganizationId));
    }

    [RequiresDatabaseFact]
    public async Task Selection_AppliesProjectAndEnvironmentFilters()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        var (projectA, envA) = await tenant.CreateProjectWithEnvironmentAsync();
        var (projectB, envB) = await tenant.CreateProjectWithEnvironmentAsync();
        string[] events = [NotificationEvents.DeploymentFailed];

        var everything = await fixture.Factory.AddChannelAsync(org, events);
        var onlyA = await fixture.Factory.AddChannelAsync(org, events, projectId: Guid.Parse(projectA.Id()));
        var onlyB = await fixture.Factory.AddChannelAsync(org, events, projectId: Guid.Parse(projectB.Id()));
        var onlyEnvA = await fixture.Factory.AddChannelAsync(org, events, environmentId: Guid.Parse(envA));
        var onlyEnvB = await fixture.Factory.AddChannelAsync(org, events, environmentId: Guid.Parse(envB));

        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.DeploymentFailed, AlertSeverity.Critical, "Deployment failed")
            { AlertKey = "deploy:filter", EnvironmentId = Guid.Parse(envA) }, default);

        var channels = (await fixture.Factory.SendsAsync(org)).Select(s => s.ChannelId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { everything.Id, onlyA.Id, onlyEnvA.Id }, channels);

        // A server-level event (no workload) never reaches a filtered channel.
        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.DeploymentFailed, AlertSeverity.Critical, "No workload") { AlertKey = "deploy:none" }, default);
        var second = (await fixture.Factory.SendsAsync(org)).Skip(3).Select(s => s.ChannelId).ToList();
        Assert.Equal([everything.Id], second);
    }

    [RequiresDatabaseFact]
    public async Task Payload_CarriesNoSecretValues_AndMasksSensitiveDetails()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org);

        await fixture.Notifier.PublishAsync(Failure(org, "mask") with { Details = new Dictionary<string, string> { ["resource"] = "web-1", ["apiKey"] = "sk-live-123", ["password"] = "hunter2" } }, default);

        var send = Assert.Single(await fixture.Factory.SendsAsync(org));
        Assert.Equal("********", send.Details!["apiKey"]);
        Assert.Equal("********", send.Details["password"]);
        var alert = Assert.Single(await fixture.Factory.AlertsAsync(org));
        Assert.DoesNotContain("sk-live-123", alert.DetailsJson);
        Assert.DoesNotContain("hunter2", alert.DetailsJson);
    }
}
