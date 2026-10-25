using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Notifications;

/// <summary>Email settings, channels, deliveries, the event catalogue and alerts over HTTP: behaviour, secrets, authorization and tenancy.</summary>
[Collection(NotificationsCollection.Name)]
public sealed class NotificationApiTests(NotificationsFixture fixture)
{
    private static readonly object SmtpSettings = new
    {
        enabled = true, transport = "smtp", smtpHost = "smtp.example.com", smtpPort = 587, smtpEncryption = "startTls", smtpUsername = "mailer",
        smtpPassword = "smtp-pass-123", fromAddress = "aethera@example.com", fromName = "Aethera",
    };

    // ---- email settings -----------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task EmailSettings_StartUnconfigured_ThenSaveWithoutEverReturningTheSecret()
    {
        var tenant = await fixture.NewTenantAsync();

        var empty = await tenant.Admin.GetJsonAsync("/api/v1/notifications/email");
        Assert.False(empty["configured"]!.GetValue<bool>());
        Assert.False(empty["enabled"]!.GetValue<bool>());

        var put = await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(SmtpSettings));
        var saved = await put.ReadAsync();
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True(saved["configured"]!.GetValue<bool>());
        Assert.True(saved["hasSmtpPassword"]!.GetValue<bool>());
        Assert.False(saved["hasResendApiKey"]!.GetValue<bool>());
        Assert.Equal("smtp.example.com", saved["smtpHost"]!.GetValue<string>());
        Assert.DoesNotContain("smtp-pass-123", saved.ToJsonString());
        Assert.DoesNotContain("smtp-pass-123", (await tenant.Admin.GetJsonAsync("/api/v1/notifications/email")).ToJsonString());

        // The password is a managed notification secret and appears nowhere in the audit trail.
        var secret = await fixture.Factory.WithDbAsync(db => db.Secrets.AsNoTracking().SingleAsync(s => s.OrganizationId == tenant.Identity.OrganizationId));
        Assert.Equal(SecretPurpose.NotificationCredential, secret.Purpose);
        foreach (var audit in await tenant.AuditAsync())
            Assert.DoesNotContain("smtp-pass-123", audit.MetadataJson);
        Assert.Contains(await tenant.AuditAsync(action: "notifications.email_updated"), _ => true);
    }

    [RequiresDatabaseFact]
    public async Task EmailSettings_PasswordSemantics_KeepReplaceRemove()
    {
        var tenant = await fixture.NewTenantAsync();
        await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(SmtpSettings));

        var kept = await (await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = true, transport = "smtp", smtpHost = "smtp2.example.com", fromAddress = "a@example.com" }))).ReadAsync();
        Assert.True(kept["hasSmtpPassword"]!.GetValue<bool>());
        Assert.Equal("smtp2.example.com", kept["smtpHost"]!.GetValue<string>());

        await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = true, transport = "smtp", smtpHost = "smtp2.example.com", fromAddress = "a@example.com", smtpPassword = "new-pass" }));
        var versions = await fixture.Factory.WithDbAsync(db => db.Secrets.AsNoTracking().SingleAsync(s => s.OrganizationId == tenant.Identity.OrganizationId));
        Assert.Equal(2, versions.CurrentVersion);

        var removed = await (await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = false, transport = "smtp", smtpHost = "smtp2.example.com", fromAddress = "a@example.com", smtpPassword = "" }))).ReadAsync();
        Assert.False(removed["hasSmtpPassword"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task EmailSettings_Validation()
    {
        var tenant = await fixture.NewTenantAsync();

        var badAddress = await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = false, transport = "smtp", fromAddress = "not an address" }));
        Assert.Contains(("/fromAddress", "email"), await badAddress.ValidationErrorsAsync());

        var badTransport = await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = false, transport = "pigeon", fromAddress = "a@example.com" }));
        Assert.Contains(("/transport", "invalid_enum"), await badTransport.ValidationErrorsAsync());

        var noHost = await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = true, transport = "smtp", fromAddress = "a@example.com" }));
        Assert.Contains(("/smtpHost", "required"), await noHost.ValidationErrorsAsync());

        var badPort = await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = false, transport = "smtp", smtpPort = 70000, fromAddress = "a@example.com" }));
        Assert.Contains(("/smtpPort", "range"), await badPort.ValidationErrorsAsync());

        var noKey = await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = true, transport = "resend", fromAddress = "a@example.com" }));
        Assert.Contains(("/resendApiKey", "required"), await noKey.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task EmailSettings_Resend_StoresTheKeyAsASecret()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = true, transport = "resend", resendApiKey = "re_abc123", fromAddress = "a@example.com" }));
        var body = await response.ReadAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body["hasResendApiKey"]!.GetValue<bool>());
        Assert.Equal("resend", body["transport"]!.GetValue<string>());
        Assert.DoesNotContain("re_abc123", body.ToJsonString());
    }

    [RequiresDatabaseFact]
    public async Task EmailTest_SendsToTheCallersAddress_AndStoresTheOutcome()
    {
        var tenant = await fixture.NewTenantAsync();
        await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(SmtpSettings));
        var email = await fixture.Factory.WithDbAsync(db => db.Users.AsNoTracking().Where(u => u.Id == tenant.Identity.UserId).Select(u => u.Email).SingleAsync());
        fixture.Email.Sent.Clear();

        var ok = await (await tenant.Admin.PostAsync("/api/v1/notifications/email/test", null)).ReadAsync();

        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal(email, ok["sentTo"]!.GetValue<string>());
        var (config, message) = Assert.Single(fixture.Email.Sent);
        Assert.Equal([email], message.To);
        Assert.Equal("smtp-pass-123", config.SmtpPassword); // decrypted for the send, never returned
        Assert.StartsWith("[Aethera] Test email", message.Subject);
        var stored = await tenant.Admin.GetJsonAsync("/api/v1/notifications/email");
        Assert.NotNull(stored["lastTestedAt"]);
        Assert.Null(stored["lastTestError"]);
    }

    [RequiresDatabaseFact]
    public async Task EmailTest_Failure_ReturnsTheErrorTextAndStoresIt()
    {
        var tenant = await fixture.NewTenantAsync();
        await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(SmtpSettings));
        fixture.Email.Failure = new EmailSendException("The SMTP server smtp.example.com:587 rejected the credentials: 535 5.7.8 Authentication failed", retryable: false);
        try
        {
            var response = await tenant.Admin.PostAsync("/api/v1/notifications/email/test", null);
            var body = await response.ReadAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(body["ok"]!.GetValue<bool>());
            Assert.Contains("Authentication failed", body["error"]!.GetValue<string>());
            Assert.DoesNotContain("smtp-pass-123", body.ToJsonString());
            Assert.Contains("Authentication failed", (await tenant.Admin.GetJsonAsync("/api/v1/notifications/email"))["lastTestError"]!.GetValue<string>());
        }
        finally
        {
            fixture.Email.Failure = null;
        }
    }

    [RequiresDatabaseFact]
    public async Task EmailTest_WithoutSettings_Is409()
    {
        var tenant = await fixture.NewTenantAsync();
        await (await tenant.Admin.PostAsync("/api/v1/notifications/email/test", null)).AssertProblemAsync(409, "notifications.email_not_configured");
    }

    [RequiresDatabaseFact]
    public async Task EmailSettings_SettingASecretNeedsTheSecretsWriteScope()
    {
        var tenant = await fixture.NewTenantAsync();
        var writeOnly = tenant.Token(OrganizationRole.Admin, "write");
        await (await writeOnly.PutAsync("/api/v1/notifications/email", Json.Body(SmtpSettings))).AssertProblemAsync(403, "auth.insufficient_scope");

        var withoutPassword = await writeOnly.PutAsync("/api/v1/notifications/email", Json.Body(new { enabled = false, transport = "smtp", smtpHost = "h.example.com", fromAddress = "a@example.com" }));
        Assert.Equal(HttpStatusCode.OK, withoutPassword.StatusCode);

        var secrets = tenant.Token(OrganizationRole.Admin, "write", "secrets:write");
        Assert.Equal(HttpStatusCode.OK, (await secrets.PutAsync("/api/v1/notifications/email", Json.Body(SmtpSettings))).StatusCode);
    }

    // ---- channels -------------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Channel_Email_CreateListGetPatchDelete()
    {
        var tenant = await fixture.NewTenantAsync();
        var created = await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "Ops", kind = "email", emailRecipients = new[] { "ops@example.com", "OPS@example.com", " dev@example.com " } });

        Assert.Equal("email", created["kind"]!.GetValue<string>());
        Assert.True(created["enabled"]!.GetValue<bool>());
        Assert.True(created["sendToMembers"]!.GetValue<bool>());
        Assert.Equal(["ops@example.com", "dev@example.com"], created["emailRecipients"]!.AsArray().Select(r => r!.GetValue<string>()));
        var events = created["events"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Equal(NotificationEvents.Defaults.Order(), events.Order()); // success events are off by default
        Assert.DoesNotContain(NotificationEvents.DeploymentSucceeded, events);
        Assert.Null(created["webhookSecret"]);

        var id = created.Id();
        var patched = await tenant.Admin.PatchOkAsync($"/api/v1/notification-channels/{id}", new { name = "Ops team", enabled = false, events = new[] { "deployment.failed", "deployment.succeeded" }, sendToMembers = false });
        Assert.Equal("Ops team", patched["name"]!.GetValue<string>());
        Assert.False(patched["enabled"]!.GetValue<bool>());
        Assert.Equal(["deployment.failed", "deployment.succeeded"], patched["events"]!.AsArray().Select(e => e!.GetValue<string>()));

        var list = await tenant.Admin.GetJsonAsync("/api/v1/notification-channels");
        Assert.Equal(id, Assert.Single(list["items"]!.AsArray())!.Id());

        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync($"/api/v1/notification-channels/{id}")).StatusCode);
        await (await tenant.Admin.GetAsync($"/api/v1/notification-channels/{id}")).AssertProblemAsync(404, "notification_channel.not_found");
        // The name is free again.
        await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "Ops team", kind = "email" });
    }

    [RequiresDatabaseFact]
    public async Task Channel_Webhook_ShowsTheSecretOnce()
    {
        var tenant = await fixture.NewTenantAsync();
        var created = await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "Chat", kind = "webhook", webhookUrl = "http://127.0.0.1:9/hook" });
        var secret = created["webhookSecret"]!.GetValue<string>();
        Assert.StartsWith("whsec_", secret);
        Assert.True(created["hasWebhookSecret"]!.GetValue<bool>());

        var got = await tenant.Admin.GetJsonAsync($"/api/v1/notification-channels/{created.Id()}");
        Assert.True(got["hasWebhookSecret"]!.GetValue<bool>());
        Assert.DoesNotContain(secret, got.ToJsonString());
        Assert.DoesNotContain(secret, (await tenant.Admin.GetJsonAsync("/api/v1/notification-channels")).ToJsonString());
        foreach (var audit in await tenant.AuditAsync()) Assert.DoesNotContain(secret, audit.MetadataJson);

        var row = await fixture.Factory.WithDbAsync(db => db.Secrets.AsNoTracking().SingleAsync(s => s.OrganizationId == tenant.Identity.OrganizationId));
        Assert.Equal(SecretPurpose.NotificationCredential, row.Purpose);

        var rotated = await (await tenant.Admin.PostAsync($"/api/v1/notification-channels/{created.Id()}/rotate-secret", null)).ReadAsync();
        Assert.StartsWith("whsec_", rotated["webhookSecret"]!.GetValue<string>());
        Assert.NotEqual(secret, rotated["webhookSecret"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Channel_Validation()
    {
        var tenant = await fixture.NewTenantAsync();

        var badEvent = await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "x", kind = "email", events = new[] { "deployment.failed", "server.exploded" } }));
        Assert.Contains(("/events", "invalid_enum"), await badEvent.ValidationErrorsAsync());

        var noEvents = await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "x", kind = "email", events = Array.Empty<string>() }));
        Assert.Contains(("/events", "required"), await noEvents.ValidationErrorsAsync());

        var badRecipient = await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "x", kind = "email", emailRecipients = new[] { "nope" } }));
        Assert.Contains(("/emailRecipients", "email"), await badRecipient.ValidationErrorsAsync());

        var noUrl = await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "x", kind = "webhook" }));
        Assert.Contains(("/webhookUrl", "required"), await noUrl.ValidationErrorsAsync());

        var badKind = await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "x", kind = "pager" }));
        Assert.Contains(("/kind", "invalid_enum"), await badKind.ValidationErrorsAsync());

        await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "dup", kind = "email" });
        await (await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "dup", kind = "email" }))).AssertProblemAsync(409, "notification_channel.already_exists");
    }

    [RequiresDatabaseFact]
    public async Task Channel_WebhookUrls_MustBePublicHttps_UnlessConfigured()
    {
        var strict = fixture.Base.WithWebHostBuilder(b => b.UseSetting("Aethera:Notifications:AllowPrivateWebhookTargets", "false"));
        var tenant = new Tenant(strict, await strict.SeedIdentityAsync());

        foreach (var url in new[] { "http://hooks.example.com/x", "https://127.0.0.1/x", "https://169.254.169.254/x", "https://localhost/x" })
        {
            var response = await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "w", kind = "webhook", webhookUrl = url }));
            Assert.Contains(("/webhookUrl", "pattern"), await response.ValidationErrorsAsync());
        }

        await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "ok", kind = "webhook", webhookUrl = "https://hooks.example.com/x" });
    }

    [RequiresDatabaseFact]
    public async Task Channel_FilterMustExist_AndTheEnvironmentMustBelongToTheProject()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var (projectA, envA) = await tenant.CreateProjectWithEnvironmentAsync();
        var (projectB, _) = await tenant.CreateProjectWithEnvironmentAsync();
        var (foreignProject, _) = await other.CreateProjectWithEnvironmentAsync();

        var created = await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "f", kind = "email", projectId = Guid.Parse(projectA.Id()), environmentId = Guid.Parse(envA) });
        Assert.Equal(projectA.Id(), created["projectId"]!.GetValue<string>());
        Assert.Equal(envA, created["environmentId"]!.GetValue<string>());

        var foreign = await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "g", kind = "email", projectId = Guid.Parse(foreignProject.Id()) }));
        Assert.Contains(("/projectId", "not_found"), await foreign.ValidationErrorsAsync());

        var mismatch = await tenant.Admin.PostAsync("/api/v1/notification-channels", Json.Body(new { name = "h", kind = "email", projectId = Guid.Parse(projectB.Id()), environmentId = Guid.Parse(envA) }));
        Assert.Contains(("/environmentId", "scope_mismatch"), await mismatch.ValidationErrorsAsync());

        var cleared = await tenant.Admin.PatchOkAsync($"/api/v1/notification-channels/{created.Id()}", new { projectId = (Guid?)null, environmentId = (Guid?)null });
        Assert.Null(cleared["projectId"]);
        Assert.Null(cleared["environmentId"]);
    }

    [RequiresDatabaseFact]
    public async Task ChannelTest_Email_DeliversInlineAndRecordsTheDelivery()
    {
        var tenant = await fixture.NewTenantAsync();
        await tenant.Admin.PutAsync("/api/v1/notifications/email", Json.Body(SmtpSettings));
        var channel = await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "t", kind = "email", sendToMembers = false, emailRecipients = new[] { "ops@example.com" } });
        fixture.Email.Sent.Clear();

        var body = await (await tenant.Admin.PostAsync($"/api/v1/notification-channels/{channel.Id()}/test", null)).ReadAsync();

        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal(["ops@example.com"], Assert.Single(fixture.Email.Sent).Message.To);
        var deliveries = await tenant.Admin.GetJsonAsync($"/api/v1/notification-deliveries?channelId={channel.Id()}");
        var delivery = Assert.Single(deliveries["items"]!.AsArray())!;
        Assert.Equal("sent", delivery["status"]!.GetValue<string>());
        Assert.Equal("test", delivery["eventType"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task ChannelTest_Failure_ReturnsTheError_AndIsInTheHistory()
    {
        var tenant = await fixture.NewTenantAsync();
        var channel = await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "t", kind = "email" }); // email is not configured

        var body = await (await tenant.Admin.PostAsync($"/api/v1/notification-channels/{channel.Id()}/test", null)).ReadAsync();

        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.Contains("not configured", body["error"]!.GetValue<string>());
        var delivery = Assert.Single((await tenant.Admin.GetJsonAsync("/api/v1/notification-deliveries"))["items"]!.AsArray())!;
        Assert.Equal("failed", delivery["status"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task NotificationEvents_ListTheCatalogueGroupedWithDefaults()
    {
        var tenant = await fixture.NewTenantAsync();
        var body = await tenant.Viewer.GetJsonAsync("/api/v1/notification-events");
        var items = body["items"]!.AsArray();

        Assert.Equal(NotificationEvents.All.Count, items.Count);
        Assert.Equal(NotificationEvents.All, items.Select(i => i!["type"]!.GetValue<string>()));
        Assert.Equal(["Deployments", "Backups", "Scheduled tasks", "Servers", "Containers", "Certificates", "Updates"], items.Select(i => i!["group"]!.GetValue<string>()).Distinct());
        Assert.All(items, i => { Assert.False(string.IsNullOrWhiteSpace(i!["label"]!.GetValue<string>())); Assert.False(string.IsNullOrWhiteSpace(i["description"]!.GetValue<string>())); });
        Assert.False(items.Single(i => i!["type"]!.GetValue<string>() == "deployment.succeeded")!["default"]!.GetValue<bool>());
        Assert.True(items.Single(i => i!["type"]!.GetValue<string>() == "deployment.failed")!["default"]!.GetValue<bool>());
    }

    // ---- alerts ---------------------------------------------------------------------------------------------------------------------------

    private async Task<(Tenant Tenant, string OpenId, string ResolvedId)> TenantWithAlertsAsync()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.ServerUnreachable, AlertSeverity.Critical, "Server a is unreachable", "No agent.")
            { ResourceType = "server", ResourceId = Guid.NewGuid(), AlertKey = "a", Path = "/servers/1", Details = new Dictionary<string, string> { ["resource"] = "a", ["lastHeartbeat"] = "12:00" } }, default);
        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.DeploymentFailed, AlertSeverity.Critical, "Deployment failed") { AlertKey = "b" }, default);
        await fixture.Notifier.PublishAsync(new NotificationEvent(org, NotificationEvents.DeploymentSucceeded, AlertSeverity.Info, "Deployment ok") { AlertKey = "b", Resolves = true }, default);
        var alerts = await fixture.Factory.AlertsAsync(org);
        return (tenant, alerts.Single(a => a.State == AlertState.Open).Id.ToString(), alerts.Single(a => a.State == AlertState.Resolved).Id.ToString());
    }

    [RequiresDatabaseFact]
    public async Task Alerts_ListByState_WithResourceAndPath()
    {
        var (tenant, openId, resolvedId) = await TenantWithAlertsAsync();

        var open = (await tenant.Viewer.GetJsonAsync("/api/v1/alerts?state=open"))["items"]!.AsArray();
        var alert = Assert.Single(open)!;
        Assert.Equal(openId, alert.Id());
        Assert.Equal("open", alert["state"]!.GetValue<string>());
        Assert.Equal("critical", alert["severity"]!.GetValue<string>());
        Assert.Equal("a", alert["resource"]!.GetValue<string>());
        Assert.Equal("/servers/1", alert["path"]!.GetValue<string>());
        Assert.Equal("12:00", alert["details"]!["lastHeartbeat"]!.GetValue<string>());
        Assert.Null(alert["acknowledgedAt"]);

        var resolved = Assert.Single((await tenant.Viewer.GetJsonAsync("/api/v1/alerts?state=resolved"))["items"]!.AsArray())!;
        Assert.Equal(resolvedId, resolved.Id());
        Assert.NotNull(resolved["resolvedAt"]);

        Assert.Equal(2, (await tenant.Viewer.GetJsonAsync("/api/v1/alerts"))["items"]!.AsArray().Count);
        var page = await tenant.Viewer.GetJsonAsync("/api/v1/alerts?limit=1");
        Assert.NotNull(page["nextCursor"]);
        await (await tenant.Viewer.GetAsync("/api/v1/alerts?state=sleeping")).AssertProblemAsync(400, "validation.invalid_parameter");
    }

    [RequiresDatabaseFact]
    public async Task Alerts_Acknowledge_IsByDevelopers_AndKeepsTheFirstAcknowledgement()
    {
        var (tenant, openId, _) = await TenantWithAlertsAsync();

        await (await tenant.Viewer.PostAsync($"/api/v1/alerts/{openId}/acknowledge", null)).AssertProblemAsync(403, "auth.forbidden");

        var first = await (await tenant.Developer.PostAsync($"/api/v1/alerts/{openId}/acknowledge", null)).ReadAsync();
        Assert.NotNull(first["acknowledgedAt"]);
        Assert.Equal(tenant.Identity.UserId.ToString(), first["acknowledgedBy"]!.GetValue<string>());
        var second = await (await tenant.Admin.PostAsync($"/api/v1/alerts/{openId}/acknowledge", null)).ReadAsync();
        Assert.Equal(first["acknowledgedAt"]!.GetValue<string>(), second["acknowledgedAt"]!.GetValue<string>());
        Assert.Equal("open", second["state"]!.GetValue<string>()); // acknowledging does not resolve
        Assert.Contains(await tenant.AuditAsync(openId, "alert.acknowledged"), _ => true);
    }

    // ---- tenancy and authorization ---------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task OtherOrganizations_CannotSeeOrTouchEachOthersData()
    {
        var (tenant, openId, _) = await TenantWithAlertsAsync();
        var other = await fixture.NewTenantAsync();
        var channel = await tenant.Admin.CreateAsync("/api/v1/notification-channels", new { name = "mine", kind = "email" });
        await tenant.Admin.PostAsync($"/api/v1/notification-channels/{channel.Id()}/test", null);

        Assert.Empty((await other.Admin.GetJsonAsync("/api/v1/alerts"))["items"]!.AsArray());
        Assert.Empty((await other.Admin.GetJsonAsync("/api/v1/notification-channels"))["items"]!.AsArray());
        Assert.Empty((await other.Admin.GetJsonAsync("/api/v1/notification-deliveries"))["items"]!.AsArray());
        Assert.Empty((await other.Admin.GetJsonAsync($"/api/v1/notification-deliveries?channelId={channel.Id()}"))["items"]!.AsArray());
        await (await other.Developer.PostAsync($"/api/v1/alerts/{openId}/acknowledge", null)).AssertProblemAsync(404, "alert.not_found");
        await (await other.Admin.GetAsync($"/api/v1/notification-channels/{channel.Id()}")).AssertProblemAsync(404, "notification_channel.not_found");
        await (await other.Admin.PatchAsync($"/api/v1/notification-channels/{channel.Id()}", new { name = "stolen" })).AssertProblemAsync(404, "notification_channel.not_found");
        await (await other.Admin.DeleteAsync($"/api/v1/notification-channels/{channel.Id()}")).AssertProblemAsync(404, "notification_channel.not_found");
        await (await other.Admin.PostAsync($"/api/v1/notification-channels/{channel.Id()}/test", null)).AssertProblemAsync(404, "notification_channel.not_found");
        Assert.False((await other.Admin.GetJsonAsync("/api/v1/notifications/email"))["configured"]!.GetValue<bool>());
    }

    // method, path, minimum role, token scope
    private static readonly (string Method, string Path, OrganizationRole Role, string Scope)[] Endpoints =
    [
        ("GET", "/notifications/email", OrganizationRole.Admin, "read"),
        ("PUT", "/notifications/email", OrganizationRole.Admin, "write"),
        ("POST", "/notifications/email/test", OrganizationRole.Admin, "write"),
        ("GET", "/notification-events", OrganizationRole.Viewer, "read"),
        ("GET", "/notification-channels", OrganizationRole.Admin, "read"),
        ("POST", "/notification-channels", OrganizationRole.Admin, "write"),
        ("GET", "/notification-channels/{id}", OrganizationRole.Admin, "read"),
        ("PATCH", "/notification-channels/{id}", OrganizationRole.Admin, "write"),
        ("DELETE", "/notification-channels/{id}", OrganizationRole.Admin, "write"),
        ("POST", "/notification-channels/{id}/test", OrganizationRole.Admin, "write"),
        ("POST", "/notification-channels/{id}/rotate-secret", OrganizationRole.Admin, "secrets:write"),
        ("GET", "/notification-deliveries", OrganizationRole.Admin, "read"),
        ("GET", "/alerts", OrganizationRole.Viewer, "read"),
        ("POST", "/alerts/{id}/acknowledge", OrganizationRole.Developer, "write"),
    ];

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path)
    {
        var url = "/api/v1" + path.Replace("{id}", Guid.NewGuid().ToString());
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT" or "PATCH") request.Content = Json.Body(new { }, method == "PATCH" ? "application/merge-patch+json" : "application/json");
        return client.SendAsync(request);
    }

    [RequiresDatabaseFact]
    public async Task EveryEndpoint_EnforcesItsRoleAndTokenScope()
    {
        var tenant = await fixture.NewTenantAsync();
        foreach (var (method, path, role, scope) in Endpoints)
        {
            foreach (var actual in Enum.GetValues<OrganizationRole>())
            {
                var response = await Send(tenant.As(actual), method, path);
                if (actual < role) await response.AssertProblemAsync(403, "auth.forbidden");
                else Assert.True(response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized), $"{method} {path} as {actual}: {(int)response.StatusCode}");
            }

            var allowedToken = await Send(tenant.Token(OrganizationRole.Owner, scope), method, path);
            Assert.True(allowedToken.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized), $"{method} {path} with scope {scope}: {(int)allowedToken.StatusCode}");

            var noScope = await Send(tenant.Token(OrganizationRole.Owner, "deploy"), method, path);
            await noScope.AssertProblemAsync(403, "auth.insufficient_scope");
        }

        var anonymous = await Send(fixture.Factory.CreateAnonymousClient(), "GET", "/alerts");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
