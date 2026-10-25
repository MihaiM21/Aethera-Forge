using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Notifications;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Tests.Notifications;

/// <summary>Message content, SMTP (against a mail sink), Resend (fake HTTP handler), webhooks (real local server) and the send job.</summary>
[Collection(NotificationsCollection.Name)]
public sealed class DeliveryTests(NotificationsFixture fixture)
{
    private static NotificationPayload Payload(string kind = NotificationKinds.Opened) => new()
    {
        Type = NotificationEvents.ServerUnreachable, Kind = kind, Severity = AlertSeverity.Critical, Title = "Server <web-1> is unreachable & down",
        Message = "No agent session since 12:00.", ResourceType = "server", ResourceId = Guid.Parse("0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55"), Path = "/servers/abc",
        Details = new Dictionary<string, string> { ["resource"] = "web-1", ["lastHeartbeat"] = "12:00", ["apiToken"] = "tok-should-not-show" },
        OccurredAt = new DateTimeOffset(2026, 10, 23, 12, 30, 0, TimeSpan.Zero),
    };

    // ---- message content --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Message_HasSubjectTextHtmlAndLink_AndNoSecrets()
    {
        var rendered = NotificationMessageBuilder.Render(Payload(), "https://aethera.example.com/");

        Assert.Equal("[Aethera] Server <web-1> is unreachable & down", rendered.Subject);
        Assert.Contains("web-1", rendered.Text);
        Assert.Contains("2026-10-23 12:30:00 UTC", rendered.Text);
        Assert.Contains("https://aethera.example.com/servers/abc", rendered.Text);
        Assert.Contains("href=\"https://aethera.example.com/servers/abc\"", rendered.Html);
        Assert.Contains("&lt;web-1&gt;", rendered.Html); // values are HTML-encoded
        Assert.DoesNotContain("<web-1>", rendered.Html);
        Assert.DoesNotContain("src=\"http", rendered.Html); // no external assets
        foreach (var body in new[] { rendered.Text, rendered.Html, rendered.WebhookJson })
            Assert.DoesNotContain("tok-should-not-show", body);

        var json = JsonNode.Parse(rendered.WebhookJson)!;
        Assert.Equal("server.unreachable", json["event"]!.GetValue<string>());
        Assert.Equal("critical", json["severity"]!.GetValue<string>());
        Assert.Equal("https://aethera.example.com/servers/abc", json["url"]!.GetValue<string>());
        Assert.Equal("web-1", json["resource"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Subject_MarksResolvedRemindersAndTests_AndHasNoLineBreaks()
    {
        Assert.EndsWith("— resolved", NotificationMessageBuilder.SubjectOf(Payload(NotificationKinds.Resolved)));
        Assert.EndsWith("— still open", NotificationMessageBuilder.SubjectOf(Payload(NotificationKinds.Reminder)));
        var multiline = Payload();
        multiline.Title = "line one\r\nBcc: attacker@example.com";
        Assert.DoesNotContain('\n', NotificationMessageBuilder.SubjectOf(multiline));
    }

    [Fact]
    public void Message_WithoutPublicUrl_HasNoLink()
    {
        var rendered = NotificationMessageBuilder.Render(Payload(), null);
        Assert.DoesNotContain("Open in Aethera", rendered.Text);
        Assert.Null(JsonNode.Parse(rendered.WebhookJson)!["url"]);
    }

    // ---- SMTP against a mail sink -----------------------------------------------------------------------------------------------------

    [RequiresSmtpFact]
    public async Task Smtp_DeliversThroughTheMailSink()
    {
        var target = Environment.GetEnvironmentVariable(RequiresSmtpFactAttribute.EnvironmentVariable)!.Split(':');
        var api = Environment.GetEnvironmentVariable("AETHERA_TEST_SMTP_API") ?? "http://127.0.0.1:8026";
        var sender = new SmtpEmailSender(Options.Create(new NotificationOptions()));
        var config = new EmailTransportConfig(EmailTransport.Smtp, "aethera@example.com", "Aethera", target[0], int.Parse(target[1]), SmtpEncryption.None, null, null, null);
        var subject = "[Aethera] smtp test " + Guid.NewGuid().ToString("N")[..8];
        var rendered = NotificationMessageBuilder.Render(Payload(), "https://aethera.example.com");

        await sender.SendAsync(config, new EmailMessage(subject, rendered.Text, rendered.Html, ["admin@example.com", "ops@example.com"]), default);

        using var http = new HttpClient();
        JsonNode? message = null;
        for (var i = 0; i < 20 && message is null; i++)
        {
            var list = JsonNode.Parse(await http.GetStringAsync($"{api}/api/v1/search?query={Uri.EscapeDataString("subject:\"" + subject + "\"")}"))!;
            message = list["messages"]?.AsArray().FirstOrDefault();
            if (message is null) await Task.Delay(100);
        }

        Assert.NotNull(message);
        var full = JsonNode.Parse(await http.GetStringAsync($"{api}/api/v1/message/{message["ID"]!.GetValue<string>()}"))!;
        Assert.Equal("aethera@example.com", full["From"]!["Address"]!.GetValue<string>());
        Assert.Equal(["admin@example.com", "ops@example.com"], full["To"]!.AsArray().Select(t => t!["Address"]!.GetValue<string>()).Order());
        Assert.Contains("web-1", full["Text"]!.GetValue<string>());
        Assert.Contains("href=\"https://aethera.example.com/servers/abc\"", full["HTML"]!.GetValue<string>());
        Assert.DoesNotContain("tok-should-not-show", full["Text"]!.GetValue<string>() + full["HTML"]!.GetValue<string>());
    }

    [Fact]
    public async Task Smtp_ConnectionRefused_IsARetryableFailureWithReadableText()
    {
        var sender = new SmtpEmailSender(Options.Create(new NotificationOptions { SmtpTimeoutSeconds = 3 }));
        var config = new EmailTransportConfig(EmailTransport.Smtp, "a@example.com", null, "127.0.0.1", 1, SmtpEncryption.None, "user", "pa55word-secret", null);

        var failure = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(config, new EmailMessage("s", "t", "<p>h</p>", ["b@example.com"]), default));

        Assert.True(failure.Retryable);
        Assert.Contains("127.0.0.1:1", failure.Message);
        Assert.DoesNotContain("pa55word-secret", failure.Message);
    }

    [Fact]
    public async Task Smtp_WithoutHost_IsRefused()
    {
        var sender = new SmtpEmailSender(Options.Create(new NotificationOptions()));
        var config = new EmailTransportConfig(EmailTransport.Smtp, "a@example.com", null, null, 25, SmtpEncryption.None, null, null, null);
        var failure = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(config, new EmailMessage("s", "t", "h", ["b@example.com"]), default));
        Assert.False(failure.Retryable);
    }

    // ---- Resend through a fake handler ------------------------------------------------------------------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static EmailTransportConfig ResendConfig(string? name = "Aethera") =>
        new(EmailTransport.Resend, "aethera@example.com", name, null, 25, SmtpEncryption.None, null, null, "re_secret_key_123");

    [Fact]
    public async Task Resend_PostsToTheApi_WithBearerKeyAndJsonBody()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"1\"}") });
        var sender = new ResendEmailSender(new HttpClient(handler));

        await sender.SendAsync(ResendConfig(), new EmailMessage("[Aethera] hi", "text", "<p>html</p>", ["a@example.com", "b@example.com"]), default);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://api.resend.com/emails", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("re_secret_key_123", request.Headers.Authorization.Parameter);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("Aethera <aethera@example.com>", json["from"]!.GetValue<string>());
        Assert.Equal(["a@example.com", "b@example.com"], json["to"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal("[Aethera] hi", json["subject"]!.GetValue<string>());
        Assert.Equal("<p>html</p>", json["html"]!.GetValue<string>());
        Assert.Equal("text", json["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(422, false)]
    [InlineData(401, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task Resend_Errors_CarryTheApiMessage_NeverTheKey(int status, bool retryable)
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("{\"name\":\"validation_error\",\"message\":\"The from domain is not verified (key re_secret_key_123)\"}") });
        var sender = new ResendEmailSender(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(ResendConfig(), new EmailMessage("s", "t", "h", ["a@example.com"]), default));

        Assert.Equal(retryable, failure.Retryable);
        Assert.Contains(status.ToString(), failure.Message);
        Assert.Contains("domain is not verified", failure.Message);
        Assert.DoesNotContain("re_secret_key_123", failure.Message);
    }

    [Fact]
    public async Task Resend_NetworkFailure_IsRetryable()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("name resolution failed"));
        var failure = await Assert.ThrowsAsync<EmailSendException>(() => new ResendEmailSender(new HttpClient(handler))
            .SendAsync(ResendConfig(), new EmailMessage("s", "t", "h", ["a@example.com"]), default));
        Assert.True(failure.Retryable);
    }

    // ---- webhooks -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Signature_IsHexHmacSha256OfTheRawBody()
    {
        const string body = "{\"event\":\"x\"}";
        const string secret = "whsec_test";
        var expected = "sha256=" + Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal(expected, WebhookSender.Sign(body, secret));
    }

    [Theory]
    [InlineData("https://hooks.example.com/x", false, null)]
    [InlineData("http://hooks.example.com/x", false, "Webhook URLs must use https.")]
    [InlineData("https://127.0.0.1/x", false, "The URL points to a private, loopback or link-local address.")]
    [InlineData("https://169.254.169.254/latest/meta-data", false, "The URL points to a private, loopback or link-local address.")]
    [InlineData("https://10.0.0.5/x", false, "The URL points to a private, loopback or link-local address.")]
    [InlineData("https://[::1]/x", false, "The URL points to a private, loopback or link-local address.")]
    [InlineData("https://localhost/x", false, "The URL points to a local host name.")]
    [InlineData("https://intranet/x", false, "The URL points to a local host name.")]
    [InlineData("https://user:pw@hooks.example.com/x", false, "The URL must not contain credentials.")]
    [InlineData("ftp://hooks.example.com/x", false, "Only http(s) URLs are allowed.")]
    [InlineData("http://127.0.0.1:8080/x", true, null)]
    public void TargetPolicy_AllowsOnlyPublicHttpsUnlessConfigured(string url, bool allowPrivate, string? refusal) =>
        Assert.Equal(refusal, WebhookTargetPolicy.Check(url, allowPrivate));

    private sealed record Captured(string Body, IReadOnlyDictionary<string, string> Headers);

    /// <summary>A real HTTP server on loopback answering with <paramref name="respond"/>; records what it received.</summary>
    private static async Task<(WebApplication App, string Url, List<Captured> Calls)> StartServerAsync(Func<HttpContext, Task> respond)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
        var app = builder.Build();
        var calls = new List<Captured>();
        app.MapPost("/hook", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            lock (calls) calls.Add(new Captured(body, context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            await respond(context);
        });
        await app.StartAsync();
        var url = app.Urls.First().Replace("127.0.0.1", "127.0.0.1") + "/hook";
        return (app, url, calls);
    }

    private static WebhookSender Sender(double timeoutSeconds = 10) =>
        new(WebhookSender.CreateClient(new NotificationOptions { AllowPrivateWebhookTargets = true }),
            Options.Create(new NotificationOptions { AllowPrivateWebhookTargets = true, WebhookTimeoutSeconds = timeoutSeconds }));

    [Fact]
    public async Task Webhook_PostsSignedJson_WithEventAndDeliveryHeaders()
    {
        var (app, url, calls) = await StartServerAsync(c => { c.Response.StatusCode = 204; return Task.CompletedTask; });
        await using var _ = app;
        var rendered = NotificationMessageBuilder.Render(Payload(), "https://aethera.example.com");
        var deliveryId = Guid.NewGuid();

        await Sender().SendAsync(url, "server.unreachable", deliveryId, rendered.WebhookJson, "whsec_abc", default);

        var call = Assert.Single(calls);
        Assert.Equal(rendered.WebhookJson, call.Body);
        Assert.Equal("server.unreachable", call.Headers["X-Aethera-Event"]);
        Assert.Equal(deliveryId.ToString(), call.Headers["X-Aethera-Delivery"]);
        Assert.Equal(WebhookSender.Sign(call.Body, "whsec_abc"), call.Headers["X-Aethera-Signature"]);
        Assert.StartsWith("sha256=", call.Headers["X-Aethera-Signature"]);
        Assert.StartsWith("application/json", call.Headers["Content-Type"]);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(429, true)]
    [InlineData(404, false)]
    [InlineData(400, false)]
    public async Task Webhook_ErrorStatuses_AreRetryableOnlyWhenTransient(int status, bool retryable)
    {
        var (app, url, _) = await StartServerAsync(c => { c.Response.StatusCode = status; return Task.CompletedTask; });
        await using var _ = app;

        var failure = await Assert.ThrowsAsync<WebhookSendException>(() => Sender().SendAsync(url, "x", Guid.NewGuid(), "{}", "whsec_abc", default));

        Assert.Equal(retryable, failure.Retryable);
        Assert.Contains(status.ToString(), failure.Message);
        Assert.DoesNotContain("whsec_abc", failure.Message);
    }

    [Fact]
    public async Task Webhook_DoesNotFollowRedirects()
    {
        var (target, targetUrl, targetCalls) = await StartServerAsync(c => Task.CompletedTask);
        await using var _1 = target;
        var (app, url, _) = await StartServerAsync(c => { c.Response.StatusCode = 302; c.Response.Headers.Location = targetUrl; return Task.CompletedTask; });
        await using var _2 = app;

        var failure = await Assert.ThrowsAsync<WebhookSendException>(() => Sender().SendAsync(url, "x", Guid.NewGuid(), "{}", "whsec_abc", default));

        Assert.False(failure.Retryable);
        Assert.Contains("redirect", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(targetCalls);
    }

    [Fact]
    public async Task Webhook_TimesOut()
    {
        var (app, url, _) = await StartServerAsync(async c => await Task.Delay(TimeSpan.FromSeconds(5), c.RequestAborted));
        await using var _ = app;

        var failure = await Assert.ThrowsAsync<WebhookSendException>(() => Sender(timeoutSeconds: 1).SendAsync(url, "x", Guid.NewGuid(), "{}", "whsec_abc", default));

        Assert.True(failure.Retryable);
        Assert.Contains("did not answer", failure.Message);
    }

    [Fact]
    public async Task Webhook_RefusesPrivateTargetsByDefault_EvenThroughDns()
    {
        var sender = new WebhookSender(WebhookSender.CreateClient(new NotificationOptions()), Options.Create(new NotificationOptions()));

        var literal = await Assert.ThrowsAsync<WebhookSendException>(() => sender.SendAsync("https://127.0.0.1/hook", "x", Guid.NewGuid(), "{}", "s", default));
        Assert.False(literal.Retryable);
        Assert.Contains("private, loopback", literal.Message);
    }

    // ---- recipients and the send job ----------------------------------------------------------------------------------------------------

    private async Task<(Tenant Tenant, string[] Members)> TenantWithMembersAsync()
    {
        var tenant = await fixture.NewTenantAsync(OrganizationRole.Owner);
        var org = tenant.Identity.OrganizationId;
        var emails = new List<string>();
        await fixture.Factory.WithDbAsync(async db =>
        {
            var owner = await db.Users.AsNoTracking().FirstAsync(u => u.Id == tenant.Identity.UserId);
            emails.Add(owner.Email);
            foreach (var role in new[] { OrganizationRole.Admin, OrganizationRole.Developer, OrganizationRole.Viewer })
            {
                var unique = Guid.NewGuid().ToString("N")[..10];
                var user = new User($"{role.ToString().ToLowerInvariant()}-{unique}@example.com", role.ToString());
                db.AddRange(user, new OrganizationMember { OrganizationId = org, User = user, Role = role });
                if (role == OrganizationRole.Admin) emails.Add(user.Email);
            }

            await db.SaveChangesAsync();
        });
        return (tenant, [.. emails]);
    }

    [RequiresDatabaseFact]
    public async Task Recipients_AreAdministratorsAndOwners_PlusTheListWithoutDuplicates()
    {
        var (tenant, members) = await TenantWithMembersAsync();
        var org = tenant.Identity.OrganizationId;
        var channel = await fixture.Factory.AddChannelAsync(org);
        channel.EmailRecipients = [members[0].ToUpperInvariant(), "extra@example.com", "extra@example.com"];

        await fixture.Factory.WithDbAsync(async db =>
        {
            var dispatcher = new NotificationDispatcher(db, null!, null!, null!, Options.Create(new NotificationOptions()), NullLogger<NotificationDispatcher>.Instance);
            var all = await dispatcher.RecipientsAsync(channel, default);
            Assert.Equal(members.Append("extra@example.com").Order(StringComparer.OrdinalIgnoreCase), all.Order(StringComparer.OrdinalIgnoreCase));

            channel.SendToMembers = false;
            Assert.Equal([members[0].ToUpperInvariant(), "extra@example.com"], await dispatcher.RecipientsAsync(channel, default));
        });
    }

    private static JobContext ContextFor(IServiceProvider services, Guid org, NotificationPayload payload, int maxAttempts)
    {
        var job = new Job { Type = Notifier.SendJobType, OrganizationId = org, MaxAttempts = maxAttempts, PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        job.Claim("test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        return new JobContext(job, services, new NullLogSink(), new SecretRedactor(), default);
    }

    private async Task SetupEmailAsync(Guid org, bool enabled = true) => await fixture.Factory.WithDbAsync(async db =>
    {
        db.EmailSettings.Add(new EmailSettings { OrganizationId = org, Enabled = enabled, FromAddress = "aethera@example.com", SmtpHost = "smtp.example.com", SmtpPort = 25, SmtpEncryption = SmtpEncryption.None });
        await db.SaveChangesAsync();
    });

    [RequiresDatabaseFact]
    public async Task SendJob_DeliversEmailToTheRecipients_AndRecordsASentDelivery()
    {
        var (tenant, members) = await TenantWithMembersAsync();
        var org = tenant.Identity.OrganizationId;
        await SetupEmailAsync(org);
        var channel = await fixture.Factory.AddChannelAsync(org);
        fixture.Email.Sent.Clear();
        var payload = Payload();
        payload.OrganizationId = org;
        payload.ChannelId = channel.Id;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await new NotificationSendJobHandler().ExecuteAsync(ContextFor(scope.ServiceProvider, org, payload, 3), default);

        var (config, message) = Assert.Single(fixture.Email.Sent);
        Assert.Equal("smtp.example.com", config.SmtpHost);
        Assert.Equal(members.Order(StringComparer.OrdinalIgnoreCase), message.To.Order(StringComparer.OrdinalIgnoreCase));
        Assert.StartsWith("[Aethera] Server", message.Subject);
        Assert.Contains("https://aethera.test/servers/abc", message.Text); // Aethera:PublicUrl, trailing slash trimmed
        var delivery = await fixture.Factory.WithDbAsync(db => db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.ChannelId == channel.Id));
        Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(NotificationEvents.ServerUnreachable, delivery.EventType);
        Assert.Null(delivery.Error);
    }

    [RequiresDatabaseFact]
    public async Task SendJob_TransientFailure_RetriesWithoutRecording_ThenRecordsTheFinalFailure()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await SetupEmailAsync(org);
        var channel = await fixture.Factory.AddChannelAsync(org);
        var payload = Payload();
        payload.OrganizationId = org;
        payload.ChannelId = channel.Id;
        fixture.Email.Failure = new EmailSendException("Could not connect to the SMTP server smtp.example.com:25: refused", retryable: true);
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var retry = await Assert.ThrowsAsync<JobFailedException>(() => new NotificationSendJobHandler().ExecuteAsync(ContextFor(scope.ServiceProvider, org, payload, 3), default));
            Assert.True(retry.Error.Retryable);
            Assert.Empty(await fixture.Factory.WithDbAsync(db => db.NotificationDeliveries.Where(d => d.ChannelId == channel.Id).ToListAsync()));

            var final = await Assert.ThrowsAsync<JobFailedException>(() => new NotificationSendJobHandler().ExecuteAsync(ContextFor(scope.ServiceProvider, org, payload, 1), default));
            Assert.True(final.Error.Retryable);
            var delivery = await fixture.Factory.WithDbAsync(db => db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.ChannelId == channel.Id));
            Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
            Assert.Contains("refused", delivery.Error);
        }
        finally
        {
            fixture.Email.Failure = null;
        }
    }

    [RequiresDatabaseFact]
    public async Task SendJob_PermanentFailure_IsRecordedAtOnce()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId; // email not configured at all
        var channel = await fixture.Factory.AddChannelAsync(org);
        var payload = Payload();
        payload.OrganizationId = org;
        payload.ChannelId = channel.Id;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var failure = await Assert.ThrowsAsync<JobFailedException>(() => new NotificationSendJobHandler().ExecuteAsync(ContextFor(scope.ServiceProvider, org, payload, 3), default));

        Assert.False(failure.Error.Retryable);
        var delivery = await fixture.Factory.WithDbAsync(db => db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.ChannelId == channel.Id));
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Contains("not configured", delivery.Error);
    }

    [RequiresDatabaseFact]
    public async Task SendJob_ForADeletedOrDisabledChannel_DoesNothing()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        var channel = await fixture.Factory.AddChannelAsync(org, enabled: false);
        var payload = Payload();
        payload.OrganizationId = org;
        payload.ChannelId = channel.Id;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await new NotificationSendJobHandler().ExecuteAsync(ContextFor(scope.ServiceProvider, org, payload, 3), default);

        Assert.Empty(await fixture.Factory.WithDbAsync(db => db.NotificationDeliveries.Where(d => d.ChannelId == channel.Id).ToListAsync()));
    }
}
