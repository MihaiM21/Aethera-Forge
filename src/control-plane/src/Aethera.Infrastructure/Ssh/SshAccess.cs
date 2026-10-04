using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Ssh;

/// <summary>Everything needed to open an SSH session to a server. Holds the decrypted credential in memory only.</summary>
public sealed record SshServerAccess(
    Guid ServerId, Guid OrganizationId, string ServerName, SshTarget Target, SshAuth Auth, string? PinnedFingerprint, Guid CredentialSecretId, int CredentialVersion)
{
    /// <summary>Changes whenever the endpoint, user, credential (version) or pinned key changes, so a pooled connection can be replaced.</summary>
    public string ConnectionKey => $"{Target}|{CredentialSecretId:N}|{CredentialVersion}|{PinnedFingerprint}";

    public override string ToString() => $"SshServerAccess({ServerId:N}, {Target})";
}

/// <summary>
/// The stored value of an SSH credential secret: a PEM private key (<c>-----BEGIN ...</c>), a JSON object
/// <c>{"privateKey":"...","passphrase":"...","password":"..."}</c>, or otherwise a plain password. Shared by the loader and the add-server endpoint.
/// </summary>
public static class SshCredentialFormat
{
    public static SshAuth Parse(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
                return new SshAuth { PrivateKey = Get("privateKey"), Passphrase = Get("passphrase"), Password = Get("password") };
            }
            catch (JsonException)
            {
                // Not JSON after all: a password that happens to start with a brace.
            }
        }

        return trimmed.StartsWith("-----BEGIN", StringComparison.Ordinal) ? new SshAuth { PrivateKey = value.Replace("\r\n", "\n") + (value.EndsWith('\n') ? "" : "\n") } : new SshAuth { Password = value };
    }

    /// <summary>The value to store for the given parts.</summary>
    public static string Compose(string? privateKey, string? passphrase, string? password)
    {
        if (!string.IsNullOrEmpty(privateKey) && string.IsNullOrEmpty(passphrase) && string.IsNullOrEmpty(password)) return privateKey.Replace("\r\n", "\n");
        if (string.IsNullOrEmpty(privateKey) && !string.IsNullOrEmpty(password) && !password.TrimStart().StartsWith('{') && !password.StartsWith("-----BEGIN", StringComparison.Ordinal)) return password;
        return JsonSerializer.Serialize(new { privateKey, passphrase, password });
    }
}

/// <summary>Reads a server's SSH endpoint and decrypts its credential. The only place (besides the secret vault) that touches the plaintext.</summary>
public sealed class SshAccessProvider(IServiceScopeFactory scopes, ISecretProtector protector, IOptions<SshOptions> options, TimeProvider time)
{
    private readonly Dictionary<Guid, (DateTimeOffset At, SshServerAccess? Access)> _cache = [];
    private readonly Lock _gate = new();

    public void Invalidate(Guid serverId)
    {
        lock (_gate) _cache.Remove(serverId);
    }

    /// <summary>The access data of a server, or null when it has no SSH credential (or does not exist).</summary>
    public async Task<SshServerAccess?> GetAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cache.TryGetValue(serverId, out var hit) && now - hit.At < TimeSpan.FromSeconds(options.Value.AccessCacheSeconds)) return hit.Access;
        }

        var access = await LoadAsync(serverId, cancellationToken);
        lock (_gate) _cache[serverId] = (now, access);
        return access;
    }

    private async Task<SshServerAccess?> LoadAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId, cancellationToken);
        if (server?.SshCredentialSecretId is not { } secretId) return null;

        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == secretId, cancellationToken);
        if (secret is null || secret.CurrentVersion == 0) return null;
        var row = await db.SecretVersions.AsNoTracking().FirstOrDefaultAsync(v => v.SecretId == secretId && v.Version == secret.CurrentVersion, cancellationToken);
        if (row is null) return null;

        byte[] plaintext;
        try
        {
            var value = new ProtectedValue(row.Ciphertext, row.Nonce, row.MasterKeyVersion) { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce };
            plaintext = protector.Unprotect(value, $"secret:{secret.Id}:v{row.Version}");
        }
        catch (CryptographicException)
        {
            throw new SshConnectException("The stored SSH credential could not be decrypted. Check the configured master key.");
        }

        try
        {
            var auth = SshCredentialFormat.Parse(Encoding.UTF8.GetString(plaintext));
            var target = new SshTarget(server.Host, server.SshPort, string.IsNullOrEmpty(server.SshUser) ? "root" : server.SshUser);
            return new SshServerAccess(server.Id, server.OrganizationId, server.Name, target, auth, server.SshHostKeyFingerprint, secretId, row.Version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
