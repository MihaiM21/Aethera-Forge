namespace Aethera.Infrastructure.Dns;

/// <summary>What a token can do: it is active, the zones it sees, and whether it can read DNS records of them.</summary>
public sealed record CloudflareTokenReport(bool Valid, bool DnsAccess, IReadOnlyList<string> Zones, string? Message);

/// <summary>A record lookup: the matching records (empty = none) and the name they were found under (an exact name or a wildcard).</summary>
public sealed record CloudflareRecordResult(IReadOnlyList<CloudflareDnsRecord> Records, string? MatchedName);

/// <summary>Token verification and record lookup on top of <see cref="ICloudflareClient"/>.</summary>
public static class CloudflareInspector
{
    public const string PermissionHint =
        "The token cannot read DNS records. Create it with the permission Zone > DNS > Edit (and Zone > Zone > Read) for the zones you use.";

    /// <summary>
    /// Verifies the token, lists its zones (paged) and probes DNS access by listing the records of the first zone. Never throws for Cloudflare
    /// failures: they come back as <see cref="CloudflareTokenReport.Message"/>.
    /// </summary>
    public static async Task<CloudflareTokenReport> InspectAsync(ICloudflareClient client, string token, CancellationToken ct)
    {
        try
        {
            // The user endpoint rejects account-owned tokens: a rejected token may still list zones, which is the real proof.
            await client.VerifyTokenAsync(token, ct);
            var zones = await client.ListZonesAsync(token, null, ct);
            var names = zones.Select(z => z.Name).Order(StringComparer.Ordinal).ToList();
            if (zones.Count == 0)
                return new CloudflareTokenReport(true, false, names, "The token is valid but sees no zones. Grant it access to the zones you use (Zone > Zone > Read).");

            try
            {
                await client.ListDnsRecordsAsync(token, zones[0].Id, null, ct);
                return new CloudflareTokenReport(true, true, names, null);
            }
            catch (CloudflareException ex) when (ex.Failure == CloudflareFailure.PermissionDenied)
            {
                return new CloudflareTokenReport(true, false, names, PermissionHint);
            }
        }
        catch (CloudflareException ex)
        {
            var message = ex.Failure switch
            {
                CloudflareFailure.InvalidToken => "Cloudflare rejected the token (invalid or expired).",
                CloudflareFailure.PermissionDenied => "The token may not list zones. Grant it Zone > Zone > Read and Zone > DNS > Edit.",
                _ => ex.Message,
            };
            return new CloudflareTokenReport(false, false, [], message);
        }
    }

    /// <summary>
    /// The A/AAAA/CNAME records for <paramref name="hostname"/> in <paramref name="zoneName"/>: those of the exact name, else those of the
    /// closest wildcard (<c>*.zone</c> fallback).
    /// </summary>
    /// <exception cref="CloudflareException">The call failed, or the token does not see the zone.</exception>
    public static async Task<CloudflareRecordResult> FindRecordsAsync(
        ICloudflareClient client, string token, string zoneName, string hostname, CancellationToken ct)
    {
        var zone = (await client.ListZonesAsync(token, zoneName, ct)).FirstOrDefault(z => z.Name == zoneName)
            ?? throw new CloudflareException(CloudflareFailure.Other, $"The token no longer sees the zone {zoneName}.");
        foreach (var candidate in CloudflareZones.CandidateNames(hostname, zoneName))
        {
            var records = await client.ListDnsRecordsAsync(token, zone.Id, candidate, ct);
            // The name filter is exact on Cloudflare's side; double-check so a lenient fake or API cannot match another name.
            var exact = records.Where(r => r.Name == candidate).ToList();
            if (exact.Count > 0) return new CloudflareRecordResult(exact, candidate);
        }

        return new CloudflareRecordResult([], null);
    }
}
