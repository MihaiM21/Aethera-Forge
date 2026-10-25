using System.Net;
using Aethera.Api.Features.Resources.DomainNames;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Domain;
using Aethera.Infrastructure.DnsProviders;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.DnsProviders;

/// <summary>How a DNS check was made: by looking up the name (<c>resolver</c>) or by reading the record through a provider's API (<c>cloudflareApi</c>).</summary>
public static class DnsCheckMethods
{
    public const string Resolver = "resolver";
    public const string CloudflareApi = "cloudflareApi";
}

/// <summary>The result of a DNS check; the caller stores it on the domain.</summary>
public sealed record DnsCheckOutcome(
    DnsStatus Status, string Method, string Message, IReadOnlyList<string> Expected, IReadOnlyList<string> Resolved);

/// <summary>
/// DNS check of a domain (ADR 0010). With a DNS provider covering the hostname's zone the record is read through the provider's API, so a
/// proxied (orange cloud) record whose origin is the server is <see cref="DnsStatus.Ok"/>. Without one the name is resolved; an answer
/// entirely inside Cloudflare's published ranges is <see cref="DnsStatus.Proxied"/> (the origin cannot be verified), not a mismatch.
/// </summary>
public sealed class DomainDnsChecker(
    AetheraDbContext db, SecretVault vault, ICloudflareClient cloudflare, IDnsResolver resolver, ILogger<DomainDnsChecker> logger)
{
    public const string ProxiedMessage = "proxied by Cloudflare: the origin cannot be verified without a DNS provider";

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(25);
    private const int MaxCnameHops = 5;

    public async Task<DnsCheckOutcome> CheckAsync(Guid organizationId, WorkloadDomain domain, CancellationToken ct)
    {
        var expected = await ExpectedAddressesAsync(domain, ct);

        string? note = null;
        if (await FindProviderAsync(organizationId, domain, ct) is { } match)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProviderTimeout);
            try
            {
                return await ViaProviderAsync(match.Provider, match.Zone, domain.Hostname, expected, timeout.Token);
            }
            catch (CloudflareException ex)
            {
                logger.LogWarning("Cloudflare lookup for domain {DomainId} failed: {Failure}", domain.Id, ex.Failure);
                note = $"The Cloudflare lookup failed ({ex.Message}); the public DNS answer is used instead.";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                note = "The Cloudflare lookup timed out; the public DNS answer is used instead.";
            }
        }

        var outcome = await ViaResolverAsync(domain, expected, ct);
        return note is null ? outcome : outcome with { Message = note + " " + outcome.Message };
    }

    // ---- through the provider's API -------------------------------------------------------------------------------------------------

    private async Task<DnsCheckOutcome> ViaProviderAsync(
        DnsProvider provider, string zone, string hostname, IReadOnlySet<string> expected, CancellationToken ct)
    {
        var secret = await db.Secrets.AsNoTracking().FirstAsync(s => s.Id == provider.ApiTokenSecretId, ct);
        var token = (await vault.RevealAsync(secret, null, ct)).Value;

        var name = hostname;
        var visited = new HashSet<string>(StringComparer.Ordinal) { name };
        for (var hop = 0; hop <= MaxCnameHops; hop++)
        {
            var found = await CloudflareInspector.FindRecordsAsync(cloudflare, token, zone, name, ct);
            if (found.Records.Count == 0)
            {
                var where = hop == 0 ? $"a record for {hostname}" : $"a record for the CNAME target {name}";
                return Outcome(DnsStatus.Missing, DnsCheckMethods.CloudflareApi, $"Cloudflare has no A, AAAA or CNAME record in {zone} matching {where}.", expected, []);
            }

            var addresses = found.Records.Where(r => r.Type is "A" or "AAAA")
                .Select(r => IPAddress.TryParse(r.Content, out var ip) ? Normalize(ip) : null).OfType<string>().ToList();
            if (addresses.Count > 0)
            {
                var resolved = new SortedSet<string>(addresses, StringComparer.Ordinal);
                var proxied = found.Records.Any(r => r.Proxied);
                if (expected.Count == 0) return Outcome(DnsStatus.Unknown, DnsCheckMethods.CloudflareApi, NoServerIp, expected, resolved);
                return resolved.Overlaps(expected)
                    ? Outcome(DnsStatus.Ok, DnsCheckMethods.CloudflareApi,
                        proxied ? "The Cloudflare record points to the server (proxied: the public answer is a Cloudflare address)." : "The Cloudflare record points to the server.",
                        expected, resolved)
                    : Outcome(DnsStatus.Mismatch, DnsCheckMethods.CloudflareApi,
                        $"The Cloudflare record points to {string.Join(", ", resolved)}, not to the server's address.", expected, resolved);
            }

            // Only CNAMEs: follow the target inside the provider's zones, else ask the resolver.
            var target = found.Records.First(r => r.Type == "CNAME").Content.Trim().TrimEnd('.').ToLowerInvariant();
            if (CloudflareZones.FindZone(target, provider.Zones) is { } targetZone)
            {
                if (!visited.Add(target)) break;
                name = target;
                zone = targetZone;
                continue;
            }

            var viaTarget = await ViaResolverAsync(target, expected, ct);
            return viaTarget with { Method = DnsCheckMethods.CloudflareApi, Message = $"{hostname} is a CNAME to {target}. {viaTarget.Message}" };
        }

        return Outcome(DnsStatus.Error, DnsCheckMethods.CloudflareApi, "The CNAME chain is too long or loops.", expected, []);
    }

    // ---- through the resolver -------------------------------------------------------------------------------------------------------

    private Task<DnsCheckOutcome> ViaResolverAsync(WorkloadDomain domain, IReadOnlySet<string> expected, CancellationToken ct) =>
        ViaResolverAsync(
            // A wildcard cannot be looked up itself: probe a name the wildcard covers.
            domain.Hostname.StartsWith("*.", StringComparison.Ordinal) ? "aethera-dns-check." + domain.Hostname[2..] : domain.Hostname, expected, ct);

    private async Task<DnsCheckOutcome> ViaResolverAsync(string probe, IReadOnlySet<string> expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            var addresses = (await resolver.ResolveAsync(probe, timeout.Token)).Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToList();
            var resolved = new SortedSet<string>(addresses.Select(Normalize), StringComparer.Ordinal);
            if (resolved.Count == 0)
                return Outcome(DnsStatus.Missing, DnsCheckMethods.Resolver, "The name does not resolve to any address (no A/AAAA records).", expected, resolved);
            if (CloudflareIpRanges.AllInside(addresses))
                return Outcome(DnsStatus.Proxied, DnsCheckMethods.Resolver, ProxiedMessage, expected, resolved);
            if (expected.Count == 0) return Outcome(DnsStatus.Unknown, DnsCheckMethods.Resolver, NoServerIp, expected, resolved);
            return resolved.Overlaps(expected)
                ? Outcome(DnsStatus.Ok, DnsCheckMethods.Resolver, "The name resolves to the server.", expected, resolved)
                : Outcome(DnsStatus.Mismatch, DnsCheckMethods.Resolver, "The name resolves, but not to the server's address.", expected, resolved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("DNS lookup failed: {ExceptionType}", ex.GetType().Name);
            return Outcome(DnsStatus.Error, DnsCheckMethods.Resolver, "The DNS lookup failed or timed out. Try again later.", expected, []);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------

    private const string NoServerIp = "The server has no public IP recorded, so the result cannot be compared. Set publicIp on the server.";

    private async Task<(DnsProvider Provider, string Zone)?> FindProviderAsync(Guid organizationId, WorkloadDomain domain, CancellationToken ct)
    {
        var providers = await db.DnsProviders.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.Kind == DnsProviderKind.Cloudflare)
            .OrderBy(p => p.CreatedAt).ToListAsync(ct);
        (DnsProvider Provider, string Zone)? best = null;
        foreach (var provider in providers)
        {
            if (CloudflareZones.FindZone(domain.Hostname, provider.Zones) is not { } zone) continue;
            // The provider chosen for the domain wins; otherwise the most specific zone (the first provider on a tie).
            if (provider.Id == domain.DnsProviderId) return (provider, zone);
            if (best is null || zone.Length > best.Value.Zone.Length) best = (provider, zone);
        }

        return best;
    }

    private async Task<SortedSet<string>> ExpectedAddressesAsync(WorkloadDomain domain, CancellationToken ct)
    {
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == domain.ServerId, ct);
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        if (server?.PublicIp is { } publicIp && IPAddress.TryParse(publicIp, out var parsedPublic)) expected.Add(Normalize(parsedPublic));
        if (server is not null && IPAddress.TryParse(server.Host, out var parsedHost)) expected.Add(Normalize(parsedHost));
        return expected;
    }

    private static string Normalize(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    private static DnsCheckOutcome Outcome(
        DnsStatus status, string method, string message, IReadOnlySet<string> expected, IReadOnlyCollection<string> resolved) =>
        new(status, method, message, expected.ToList(), resolved.ToList());
}
