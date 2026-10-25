using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Proxy;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// Builds the command that makes a server's proxy present and configured (ADR 0010). Besides the instance's ACME settings it adds the DNS-01
/// resolver (<c>cloudflare</c>) and its token when a domain of the server uses a DNS provider. Used by every deployment with domains and by
/// <c>POST /servers/{id}/proxy/ensure</c>, so a rotated token or a newly used provider reaches the proxy with the next ensure.
/// </summary>
public static class ProxyConfigurator
{
    /// <summary>Shown next to HTTPS domains while no ACME email is configured.</summary>
    public const string NoAcmeEmailWarning =
        "No ACME email configured: HTTPS routes use Traefik's default certificate (browsers warn). Set it with the installer's --acme-email option or Aethera:Proxy:AcmeEmail.";

    public static bool AcmeAvailable(ProxyOptions options) => !string.IsNullOrWhiteSpace(options.AcmeEmail);

    /// <summary>
    /// The ensure command for <paramref name="serverId"/>. <paramref name="onSecret"/> receives the DNS token so the caller can register it for
    /// log redaction.
    /// </summary>
    public static async Task<ProxyEnsureCommand> BuildEnsureCommandAsync(
        AetheraDbContext db, ISecretProtector protector, IProxyProvider proxy, ProxyOptions options, Guid serverId, Action<string>? onSecret, CancellationToken ct)
    {
        DnsChallengeSettings? dns = null;
        if (AcmeAvailable(options))
        {
            // One Cloudflare token per proxy (CF_DNS_API_TOKEN): the oldest provider that a domain of this server uses.
            var provider = await db.Domains.AsNoTracking()
                .Where(d => d.ServerId == serverId && d.DnsProviderId != null && d.DnsProvider!.DeletedAt == null)
                .Select(d => d.DnsProvider!).OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).FirstOrDefaultAsync(ct);
            if (provider is not null)
            {
                var token = await ReadAsync(db, protector, provider.ApiTokenSecretId, ct);
                if (token is not null)
                {
                    onSecret?.Invoke(token);
                    dns = new DnsChallengeSettings(TraefikProxyProvider.CloudflareResolver, token);
                }
            }
        }

        return proxy.BuildEnsureCommand(new ProxySettings(options.AcmeEmail, options.Network, options.Version, options.Staging) { DnsChallenge = dns });
    }

    /// <summary>The current plaintext of a secret, or null when it no longer exists. A secret that cannot be decrypted (wrong master key) is an error.</summary>
    private static async Task<string?> ReadAsync(AetheraDbContext db, ISecretProtector protector, Guid secretId, CancellationToken ct)
    {
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == secretId, ct);
        if (secret is null) return null;
        var row = await db.SecretVersions.AsNoTracking().FirstOrDefaultAsync(v => v.SecretId == secretId && v.Version == secret.CurrentVersion, ct);
        if (row is null) return null;
        var value = new ProtectedValue(row.Ciphertext, row.Nonce, row.MasterKeyVersion) { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce };
        byte[] plaintext;
        try
        {
            plaintext = protector.Unprotect(value, $"secret:{secret.Id}:v{row.Version}");
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The DNS provider's token could not be decrypted. Check the configured master key.");
        }

        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
