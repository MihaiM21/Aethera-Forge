using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Crypto;

/// <summary>How strictly <see cref="MasterKeyring.Load"/> insists on a configured key.</summary>
public enum KeyringMode
{
    /// <summary>A missing key is a startup error (production and any unknown environment).</summary>
    Strict,

    /// <summary>A missing key is replaced by an ephemeral random key and a loud warning (local development).</summary>
    Development,

    /// <summary>A missing key is replaced by the fixed, public test key (automated tests only).</summary>
    Testing,
}

/// <summary>
/// The master keys that wrap per-value data keys: a keyring of 32-byte keys by version, one of which is current (used for new
/// values). Older versions stay available so existing values remain readable after a rotation.
/// </summary>
/// <remarks>
/// Configuration (all values are base64 of exactly 32 bytes; generate one with <c>openssl rand -base64 32</c>):
/// <list type="bullet">
/// <item><c>Aethera:Security:MasterKey</c> (environment <c>AETHERA_MASTER_KEY</c>): the single key. It is version
/// <c>Aethera:Security:MasterKeyVersion</c> (default 1).</item>
/// <item><c>Aethera:Security:MasterKeys:{version}</c>: a keyring. To rotate, add a new version and set
/// <c>Aethera:Security:CurrentMasterKeyVersion</c> to it; keep the old ones as long as values encrypted with them exist.</item>
/// </list>
/// Key bytes are never logged and never part of an exception message; errors name the configuration key only.
/// </remarks>
public sealed class MasterKeyring : IDisposable
{
    public const string MasterKeyConfigKey = "Aethera:Security:MasterKey";
    public const string MasterKeyVersionConfigKey = "Aethera:Security:MasterKeyVersion";
    public const string KeyringConfigSection = "Aethera:Security:MasterKeys";
    public const string CurrentVersionConfigKey = "Aethera:Security:CurrentMasterKeyVersion";
    public const string EnvironmentVariableName = "AETHERA_MASTER_KEY";
    public const int KeyLength = 32;

    /// <summary>The key used by <see cref="KeyringMode.Testing"/>. Public on purpose: it must never protect real data.</summary>
    public static readonly byte[] TestKey = SHA256.HashData("aethera-testing-master-key-do-not-use"u8);

    private readonly Dictionary<int, byte[]> _keys;

    public MasterKeyring(IReadOnlyDictionary<int, byte[]> keys, int currentVersion)
    {
        if (keys.Count == 0) throw new ArgumentException("At least one master key is required.", nameof(keys));
        if (!keys.ContainsKey(currentVersion)) throw new ArgumentException("The current master key version is not in the keyring.", nameof(currentVersion));
        foreach (var (version, key) in keys)
        {
            if (version < 1) throw new ArgumentException("Master key versions start at 1.", nameof(keys));
            if (key.Length != KeyLength) throw new ArgumentException($"Master key version {version} must be {KeyLength} bytes.", nameof(keys));
        }

        _keys = keys.ToDictionary(k => k.Key, k => (byte[])k.Value.Clone());
        CurrentVersion = currentVersion;
    }

    /// <summary>Version used to protect new values.</summary>
    public int CurrentVersion { get; }

    public IReadOnlyCollection<int> Versions => _keys.Keys;

    public bool TryGet(int version, out byte[] key)
    {
        var found = _keys.TryGetValue(version, out var value);
        key = value ?? [];
        return found;
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>Reads the keyring from configuration (and the <c>AETHERA_MASTER_KEY</c> environment variable).</summary>
    /// <exception cref="InvalidOperationException">No key in <see cref="KeyringMode.Strict"/> mode, or a malformed key. The message never contains key material.</exception>
    public static MasterKeyring Load(IConfiguration configuration, KeyringMode mode, ILogger? logger = null)
    {
        var keys = new Dictionary<int, byte[]>();

        foreach (var entry in configuration.GetSection(KeyringConfigSection).GetChildren())
        {
            if (!int.TryParse(entry.Key, out var version) || version < 1)
                throw new InvalidOperationException($"{KeyringConfigSection}:{entry.Key}: key versions must be positive integers.");
            if (string.IsNullOrWhiteSpace(entry.Value)) continue;
            keys[version] = Decode(entry.Value, $"{KeyringConfigSection}:{entry.Key}");
        }

        var single = configuration[MasterKeyConfigKey];
        if (string.IsNullOrWhiteSpace(single)) single = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        int? singleVersion = null;
        if (!string.IsNullOrWhiteSpace(single))
        {
            var version = 1;
            if (configuration[MasterKeyVersionConfigKey] is { Length: > 0 } text && (!int.TryParse(text, out version) || version < 1))
                throw new InvalidOperationException($"{MasterKeyVersionConfigKey} must be a positive integer.");
            var decoded = Decode(single, MasterKeyConfigKey);
            if (keys.TryGetValue(version, out var existing) && !CryptographicOperations.FixedTimeEquals(existing, decoded))
                throw new InvalidOperationException(
                    $"{MasterKeyConfigKey} and {KeyringConfigSection}:{version} configure different keys for version {version}.");
            keys[version] = decoded;
            singleVersion = version;
        }

        if (keys.Count == 0)
        {
            switch (mode)
            {
                case KeyringMode.Development:
                    logger?.LogWarning(
                        "{EnvVar} is not set: using an EPHEMERAL master key generated for this process. Secrets created now become "
                        + "unreadable after a restart. Set {EnvVar} (base64 of 32 bytes, e.g. `openssl rand -base64 32`) to keep them",
                        EnvironmentVariableName, EnvironmentVariableName);
                    keys[1] = RandomNumberGenerator.GetBytes(KeyLength);
                    singleVersion = 1;
                    break;
                case KeyringMode.Testing:
                    keys[1] = (byte[])TestKey.Clone();
                    singleVersion = 1;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"No master key is configured. Set {EnvironmentVariableName} (or {MasterKeyConfigKey}) to the base64 encoding of "
                        + $"{KeyLength} random bytes, for example the output of `openssl rand -base64 32`. Secrets cannot be stored without it.");
            }
        }

        var current = singleVersion ?? keys.Keys.Max();
        if (configuration[CurrentVersionConfigKey] is { Length: > 0 } configured)
        {
            if (!int.TryParse(configured, out current) || !keys.ContainsKey(current))
                throw new InvalidOperationException($"{CurrentVersionConfigKey} must name a configured master key version.");
        }

        return new MasterKeyring(keys, current);
    }

    private static byte[] Decode(string base64, string configKey)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            if (bytes.Length == KeyLength) return bytes;
            CryptographicOperations.ZeroMemory(bytes);
        }
        catch (FormatException)
        {
            // fall through: do not echo the value or the parser message
        }

        throw new InvalidOperationException($"{configKey} must be the base64 encoding of exactly {KeyLength} bytes (for example `openssl rand -base64 32`).");
    }
}
