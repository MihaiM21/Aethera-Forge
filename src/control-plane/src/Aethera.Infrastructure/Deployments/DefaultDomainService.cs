using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// Automatic domains (ADR 0011), the way Coolify does it: a new workload gets <c>&lt;slug&gt;-&lt;6 hex&gt;.&lt;base&gt;</c>, where the base is its server's
/// wildcard domain or, without one, <c>&lt;public ip&gt;.sslip.io</c> (a public DNS service that answers any such name with the IP in it). It is an
/// ordinary domain row (<see cref="WorkloadDomain.IsAutomatic"/>), so it can be edited or deleted; <see cref="Workload.AutoDomainAssigned"/>
/// makes sure a deleted one never comes back. HTTPS when the proxy has an ACME email.
/// </summary>
public static partial class DefaultDomainService
{
    /// <summary>The public wildcard DNS service used when a server has no wildcard domain of its own.</summary>
    public const string SslipSuffix = "sslip.io";

    [GeneratedRegex(@"\A[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex LabelPattern();

    /// <summary>Why no domain was created, for the deployment log; null when one was (or the workload already had one).</summary>
    public sealed record Outcome(WorkloadDomain? Domain, string? SkippedBecause);

    /// <summary>
    /// Adds the automatic domain of <paramref name="workload"/> to <paramref name="db"/> (not saved) unless it has one already, one was created
    /// before, or it has domains. <paramref name="composeService"/> is the service it routes to for a compose application.
    /// </summary>
    public static async Task<Outcome> AssignAsync(
        AetheraDbContext db, Workload workload, string? composeService, bool https, CancellationToken ct)
    {
        if (workload.AutoDomainAssigned) return new Outcome(null, null);
        if (workload.Domains.Count > 0 || await db.Domains.AnyAsync(d => d.WorkloadId == workload.Id, ct))
        {
            workload.AutoDomainAssigned = true; // the user routed it themselves
            return new Outcome(null, null);
        }

        var server = workload.Server ?? await db.Servers.FirstOrDefaultAsync(s => s.Id == workload.ServerId, ct);
        if (server is null) return new Outcome(null, "the server no longer exists");
        var suffix = KnownBaseDomain(server);
        if (suffix is null)
            return new Outcome(null, $"the server {server.Name} has neither a wildcard domain nor a known public IP address (set one in its settings)");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var hostname = $"{Label(workload.Slug)}.{suffix}";
            if (hostname.Length > 253) return new Outcome(null, "the wildcard domain is too long for an automatic domain");
            if (await db.Domains.AnyAsync(d => d.Hostname == hostname && d.PathPrefix == "/", ct)) continue;

            var domain = new WorkloadDomain
            {
                WorkloadId = workload.Id, ServerId = server.Id, PathPrefix = "/", HttpsEnabled = https, IsPrimary = true, IsAutomatic = true,
                ComposeService = composeService,
            };
            domain.SetHostname(hostname);
            db.Domains.Add(domain);
            workload.AutoDomainAssigned = true;
            return new Outcome(domain, null);
        }

        return new Outcome(null, "no free automatic hostname was found");
    }

    /// <summary>
    /// The base of a server's automatic domains: its wildcard domain, else <c>&lt;ip with dashes&gt;.sslip.io</c> from its public IP (or a host that is
    /// an IP address); null when neither is known. No DNS lookup: a host name may resolve differently for the world than for the control plane.
    /// </summary>
    public static string? KnownBaseDomain(Server server) =>
        server.WildcardDomain is { Length: > 0 } wildcard ? wildcard
        : (Parse(server.PublicIp) ?? Parse(server.Host)) is { } ip ? $"{SslipLabel(ip)}.{SslipSuffix}"
        : null;

    /// <summary>The sslip.io form of an address: dots (IPv4) or colons (IPv6) become dashes, <c>203-0-113-7</c>, <c>2001-db8--1</c>.</summary>
    public static string SslipLabel(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var text = ip.ToString().Replace('.', '-').Replace(':', '-');
        // A label must not start or end with a dash: sslip.io reads a leading or trailing "0" the same.
        if (text.StartsWith('-')) text = "0" + text;
        if (text.EndsWith('-')) text += "0";
        return text;
    }

    /// <summary><c>&lt;slug&gt;-&lt;6 random hex digits&gt;</c>, at most 63 characters.</summary>
    private static string Label(string slug)
    {
        var clean = new string(slug.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray()).Trim('-');
        if (clean.Length == 0) clean = "app";
        if (clean.Length > 56) clean = clean[..56].TrimEnd('-');
        var label = $"{clean}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
        return LabelPattern().IsMatch(label) ? label : $"app-{label[^6..]}";
    }

    private static IPAddress? Parse(string? text) =>
        text is { Length: > 0 } && IPAddress.TryParse(text.Trim().Trim('[', ']'), out var ip) ? ip : null;
}
