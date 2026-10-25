using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Notifications.Detectors;

/// <summary>
/// What a TLS handshake showed. <see cref="Connected"/> false = no TLS answer at all (refused, timeout, no TLS on that port): says nothing about the
/// certificate. <see cref="CertificateError"/> is set when a certificate was presented but is not valid for the host.
/// </summary>
public sealed record CertificateProbeResult(bool Connected, DateTimeOffset? NotAfter = null, string? CertificateError = null, string? ConnectError = null);

/// <summary>Performs the TLS handshake of the certificate check (replaced by a fake in tests).</summary>
public interface ICertificateProbe
{
    Task<CertificateProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>A handshake with SNI from the control plane to <c>host:443</c>; the validation callback only records what it saw.</summary>
public sealed class TlsCertificateProbe : ICertificateProbe
{
    public async Task<CertificateProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var errors = SslPolicyErrors.None;
        DateTimeOffset? notAfter = null;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, deadline.Token);
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, policyErrors) =>
            {
                errors = policyErrors;
                if (certificate is not null) notAfter = new X509Certificate2(certificate).NotAfter.ToUniversalTime();
                return true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new CertificateProbeResult(false, ConnectError: $"No answer from {host}:{port} within {timeout.TotalSeconds:0.#} seconds.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            return new CertificateProbeResult(false, ConnectError: $"Could not complete a TLS handshake with {host}:{port}: {ex.Message}");
        }

        string? error = null;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) error = "The server presented no certificate.";
        else if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) error = $"The certificate is not valid for {host}.";
        else if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) error = "The certificate is not trusted (self-signed, unknown issuer or expired).";
        return new CertificateProbeResult(true, notAfter, error);
    }
}

/// <summary>
/// <c>certificate.expiring</c> / <c>certificate.failed</c>: once a day (<see cref="NotificationOptions.CertificateCheckHours"/>) every live HTTPS domain
/// gets a TLS handshake with SNI to <c>hostname:443</c>. The outcome is recorded on the domain (<c>certificate_status</c>, <c>certificate_expires_at</c>,
/// <c>certificate_error</c>). A certificate that expires within <see cref="NotificationOptions.CertificateWarnDays"/> raises <c>certificate.expiring</c>
/// (resolved once it is renewed); a certificate error that has lasted <see cref="NotificationOptions.CertificateFailedHours"/> raises
/// <c>certificate.failed</c> (resolved when the certificate is valid again). A host that does not answer at all changes nothing.
/// </summary>
public sealed class CertificateDetector(IServiceScopeFactory scopes, Notifier notifier, ICertificateProbe probe, IClock clock, IOptions<NotificationOptions> options) : IDetector
{
    public const string ExpiringPrefix = "cert-expiring:";
    public const string FailedPrefix = "cert-failed:";
    private const int Parallelism = 8;

    public string Name => "certificates";

    public TimeSpan Interval => TimeSpan.FromHours(Math.Max(0.001, options.Value.CertificateCheckHours));

    private sealed record Target(Guid DomainId, string Host, Guid WorkloadId, string WorkloadName, Guid EnvironmentId, Guid OrganizationId);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();

        var targets = await db.Domains.AsNoTracking()
            .Where(d => d.HttpsEnabled && d.Workload.DeletedAt == null && d.Workload.DesiredState == DesiredState.Running && d.Workload.CurrentDeploymentId != null)
            .Select(d => new Target(d.Id, d.Hostname, d.WorkloadId, d.Workload.Name, d.Workload.EnvironmentId, d.Workload.Environment.Project.OrganizationId))
            .ToListAsync(cancellationToken);
        targets = targets.Where(t => !t.Host.StartsWith('*') && !System.Net.IPAddress.TryParse(t.Host, out _)).ToList();

        // Handshakes run side by side; the results are applied one by one (one DbContext).
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.CertificateTimeoutSeconds));
        using var gate = new SemaphoreSlim(Parallelism);
        var results = await Task.WhenAll(targets.Select(async t =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return (Target: t, Result: await probe.ProbeAsync(t.Host, 443, timeout, cancellationToken)); }
            finally { gate.Release(); }
        }));

        var openExpiring = await DetectorSupport.OpenKeysAsync(db, ExpiringPrefix, cancellationToken);
        var openFailed = await DetectorSupport.OpenKeysAsync(db, FailedPrefix, cancellationToken);
        foreach (var (target, result) in results)
            await ApplyAsync(db, target, result, openExpiring, openFailed, cancellationToken);
    }

    private async Task ApplyAsync(
        AetheraDbContext db, Target target, CertificateProbeResult result, HashSet<string> openExpiring, HashSet<string> openFailed, CancellationToken cancellationToken)
    {
        if (!result.Connected) return;
        var settings = options.Value;
        var now = clock.UtcNow;
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == target.DomainId, cancellationToken);
        if (domain is null) return;

        var expired = result.NotAfter is { } na && na <= now;
        var status = result.CertificateError is not null ? CertificateStatus.Failed : expired ? CertificateStatus.Expired : CertificateStatus.Issued;
        var error = result.CertificateError ?? (expired ? "The certificate has expired." : null);

        var before = domain.CertificateStatus;
        domain.CertificateStatus = status;
        domain.CertificateExpiresAt = result.NotAfter;
        domain.CertificateError = error;
        if (before != status)
            db.ResourceEvents.Add(new ResourceEvent
            {
                ResourceType = "domain", ResourceId = domain.Id, Kind = "certificate.status", Axis = "certificate",
                OldValue = before.ToString().ToLowerInvariant(), NewValue = status.ToString().ToLowerInvariant(), Detail = error, OccurredAt = now,
            });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return; } // edited meanwhile: the next run decides again

        var details = new Dictionary<string, string> { ["resource"] = target.Host, ["application"] = target.WorkloadName };
        if (result.NotAfter is { } expires) details["expires"] = expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

        NotificationEvent Event(string type, AlertSeverity severity, string title, string message, string key, bool resolves = false) =>
            new(target.OrganizationId, type, severity, title, message)
            {
                ResourceType = "domain", ResourceId = target.DomainId, EnvironmentId = target.EnvironmentId, Path = "/domains", AlertKey = key, Resolves = resolves, Details = details,
            };

        // Expiry.
        var expiringKey = ExpiringPrefix + target.DomainId;
        var remaining = result.NotAfter is { } until ? until - now : (TimeSpan?)null;
        if (status != CertificateStatus.Failed && remaining is { } left && left <= TimeSpan.FromDays(Math.Max(0, settings.CertificateWarnDays)))
        {
            var days = (int)Math.Floor(left.TotalDays);
            await notifier.PublishAsync(Event(NotificationEvents.CertificateExpiring, left <= TimeSpan.FromDays(3) ? AlertSeverity.Critical : AlertSeverity.Warning,
                left <= TimeSpan.Zero ? $"The certificate of {target.Host} has expired" : $"The certificate of {target.Host} expires in {days} day(s)",
                left <= TimeSpan.Zero ? "Visitors get a certificate warning. Check that the domain points at this server and that Let's Encrypt can issue a certificate."
                    : "The proxy renews certificates automatically; if this stays open, renewal is failing (DNS, port 80/443 or rate limits).", expiringKey), cancellationToken);
        }
        else if (openExpiring.Contains(expiringKey) && status == CertificateStatus.Issued)
        {
            await notifier.PublishAsync(Event(NotificationEvents.CertificateExpiring, AlertSeverity.Info, $"The certificate of {target.Host} was renewed",
                "The certificate is valid for more than the warning period again.", expiringKey, resolves: true), cancellationToken);
        }

        // A certificate error that persists.
        var failedKey = FailedPrefix + target.DomainId;
        if (status == CertificateStatus.Failed)
        {
            var since = await db.ResourceEvents.AsNoTracking()
                .Where(e => e.ResourceType == "domain" && e.ResourceId == target.DomainId && e.Axis == "certificate" && e.NewValue == "failed")
                .OrderByDescending(e => e.OccurredAt).Select(e => (DateTimeOffset?)e.OccurredAt).FirstOrDefaultAsync(cancellationToken) ?? now;
            if (now - since >= TimeSpan.FromHours(Math.Max(0, settings.CertificateFailedHours)))
            {
                await notifier.PublishAsync(Event(NotificationEvents.CertificateFailed, AlertSeverity.Critical, $"The certificate of {target.Host} is not valid",
                    $"{error} This has lasted since {since.UtcDateTime:yyyy-MM-dd HH:mm} UTC.", failedKey), cancellationToken);
            }
        }
        else if (openFailed.Contains(failedKey) && status == CertificateStatus.Issued)
        {
            await notifier.PublishAsync(Event(NotificationEvents.CertificateFailed, AlertSeverity.Info, $"The certificate of {target.Host} is valid again",
                "The TLS handshake now shows a valid certificate.", failedKey, resolves: true), cancellationToken);
        }
    }
}
