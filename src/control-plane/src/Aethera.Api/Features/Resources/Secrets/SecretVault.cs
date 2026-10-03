using System.Security.Cryptography;
using System.Text;
using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Secrets;

/// <summary>
/// Creates, versions and reads secret values through <see cref="ISecretProtector"/>. The only place that touches plaintext. Plaintext byte
/// buffers are zeroed as soon as they are encrypted or decoded; nothing here logs or returns values except <see cref="RevealAsync"/>.
/// </summary>
public sealed class SecretVault(AetheraDbContext db, ISecretProtector protector, IClock clock)
{
    public static string AssociatedData(Guid secretId, int version) => $"secret:{secretId}:v{version}";

    /// <summary>Adds a new secret with its first version to the context (not saved).</summary>
    public Secret Create(Guid organizationId, string name, string? description, string value, Guid? projectId = null, Guid? environmentId = null,
        Guid? workloadId = null)
    {
        var secret = new Secret
        {
            OrganizationId = organizationId, Name = name, Description = description,
            ProjectId = projectId, EnvironmentId = environmentId, WorkloadId = workloadId,
        };
        db.Secrets.Add(secret);
        AddVersion(secret, value);
        return secret;
    }

    /// <summary>Encrypts <paramref name="value"/> as the next version of <paramref name="secret"/> (not saved).</summary>
    public SecretVersion AddVersion(Secret secret, string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            var next = secret.CurrentVersion + 1;
            var protectedValue = protector.Protect(plaintext, AssociatedData(secret.Id, next));
            var version = secret.AddVersion(protectedValue.Ciphertext, protectedValue.Nonce, protectedValue.WrappedDataKey!,
                protectedValue.WrappedDataKeyNonce!, protectedValue.KeyVersion, clock.UtcNow);
            db.SecretVersions.Add(version);
            return version;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Decrypts a version (the current one by default). Callers must authorize and audit; never log the result.</summary>
    public async Task<(int Version, string Value)> RevealAsync(Secret secret, int? version, CancellationToken ct)
    {
        var wanted = version ?? secret.CurrentVersion;
        var row = await db.SecretVersions.AsNoTracking().FirstOrDefaultAsync(v => v.SecretId == secret.Id && v.Version == wanted, ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound("secret_version", wanted));
        var value = new ProtectedValue(row.Ciphertext, row.Nonce, row.MasterKeyVersion)
        {
            WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce,
        };

        byte[] plaintext;
        try
        {
            plaintext = protector.Unprotect(value, AssociatedData(secret.Id, row.Version));
        }
        catch (CryptographicException)
        {
            // Wrong/missing master key or tampered row: say so without detail.
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status500InternalServerError, "secret.undecryptable",
                "The secret could not be decrypted. Check the configured master key.", "Secret cannot be decrypted"));
        }

        try
        {
            return (row.Version, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>A random URL-safe password of <paramref name="length"/> characters from a cryptographically secure source.</summary>
    public static string GeneratePassword(int length = 32)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }
}
