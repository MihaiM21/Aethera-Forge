using System.Text.Json;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Notifications;
using Aethera.Infrastructure.Notifications.Detectors;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Notifications;

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>An <see cref="IEmailSender"/> that records what it was asked to send, and fails on demand.</summary>
public sealed class RecordingEmailSender : IEmailSender
{
    public List<(EmailTransportConfig Config, EmailMessage Message)> Sent { get; } = [];

    public EmailSendException? Failure { get; set; }

    public Task SendAsync(EmailTransportConfig config, EmailMessage message, CancellationToken cancellationToken)
    {
        if (Failure is not null) throw Failure;
        lock (Sent) Sent.Add((config, message));
        return Task.CompletedTask;
    }
}

public sealed class FakeCertificateProbe : ICertificateProbe
{
    public Dictionary<string, CertificateProbeResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Probed { get; } = [];

    public Task<CertificateProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Probed) Probed.Add($"{host}:{port}");
        return Task.FromResult(Results.TryGetValue(host, out var result) ? result : new CertificateProbeResult(false, ConnectError: "no answer"));
    }
}

/// <summary>Skips unless <c>AETHERA_TEST_SMTP</c> (host:port of a mail sink such as mailpit) is set.</summary>
public sealed class RequiresSmtpFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "AETHERA_TEST_SMTP";

    public RequiresSmtpFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"{EnvironmentVariable} is not set (host:port of a mail sink, e.g. 127.0.0.1:1026 for axllent/mailpit).";
    }
}

/// <summary>One API factory (and database) with a fake clock, a recording mail sender and a fake certificate probe, shared by the notification tests.</summary>
public sealed class NotificationsFixture : IDisposable
{
    public NotificationsFixture()
    {
        Clock = new FakeClock(DateTimeOffset.UtcNow);
        Base = new AetheraApiFactory();
        Factory = Base.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Aethera:Notifications:AllowPrivateWebhookTargets", "true");
            builder.UseSetting("Aethera:Notifications:ContainerSettleSeconds", "0");
            builder.UseSetting("Aethera:PublicUrl", "https://aethera.test/");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClock>(Clock);
                services.AddSingleton<IEmailSender>(Email);
                services.AddSingleton<ICertificateProbe>(Probe);
            });
        });
    }

    public AetheraApiFactory Base { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeClock Clock { get; }

    public RecordingEmailSender Email { get; } = new();

    public FakeCertificateProbe Probe { get; } = new();

    public async Task<Tenant> NewTenantAsync(OrganizationRole role = OrganizationRole.Owner) => new(Factory, await Factory.SeedIdentityAsync(role));

    public INotifier Notifier => Factory.Services.GetRequiredService<INotifier>();

    public T Detector<T>() where T : class, IDetector => Factory.Services.GetRequiredService<T>();

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class NotificationsCollection : ICollectionFixture<NotificationsFixture>
{
    public const string Name = "notifications";
}

internal static class NotificationTestExtensions
{
    public static async Task<T> WithDbAsync<T>(this WebApplicationFactory<Program> factory, Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    public static async Task WithDbAsync(this WebApplicationFactory<Program> factory, Func<AetheraDbContext, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    /// <summary>Inserts a channel straight into the database (the API paths have their own tests).</summary>
    public static async Task<NotificationChannel> AddChannelAsync(
        this WebApplicationFactory<Program> factory, Guid org, string[]? events = null, bool enabled = true, Guid? projectId = null, Guid? environmentId = null,
        NotificationChannelKind kind = NotificationChannelKind.Email, string? name = null)
    {
        var channel = new NotificationChannel
        {
            OrganizationId = org, Name = name ?? "ch-" + Guid.NewGuid().ToString("N")[..8], Kind = kind, Enabled = enabled,
            Events = [.. events ?? NotificationEvents.Defaults], ProjectId = projectId, EnvironmentId = environmentId,
        };
        await factory.WithDbAsync(async db =>
        {
            if (kind == NotificationChannelKind.Webhook)
            {
                var secret = new Secret { OrganizationId = org, Name = "notifications/webhook/" + channel.Id.ToString("N")[^12..], Purpose = SecretPurpose.NotificationCredential };
                db.Secrets.Add(secret);
                channel.WebhookUrl = "http://127.0.0.1:9/hook";
                channel.WebhookSecretId = secret.Id;
            }

            db.NotificationChannels.Add(channel);
            await db.SaveChangesAsync();
        });
        return channel;
    }

    public static Task<List<NotificationPayload>> SendsAsync(this WebApplicationFactory<Program> factory, Guid org) =>
        factory.WithDbAsync(async db =>
            (await db.Jobs.AsNoTracking().Where(j => j.OrganizationId == org && j.Type == Notifier.SendJobType).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToListAsync())
            .Select(j => JsonSerializer.Deserialize<NotificationPayload>(j.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToList());

    public static Task<List<Alert>> AlertsAsync(this WebApplicationFactory<Program> factory, Guid org) =>
        factory.WithDbAsync(db => db.Alerts.AsNoTracking().Where(a => a.OrganizationId == org).OrderBy(a => a.OpenedAt).ThenBy(a => a.Id).ToListAsync());
}

/// <summary>A log sink that keeps nothing: the notification job handler only narrates.</summary>
public sealed class NullLogSink : ILogSink
{
    public string StreamId => "test";

    public ValueTask WriteAsync(LogStream stream, string line, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask WriteSystemAsync(string line, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
