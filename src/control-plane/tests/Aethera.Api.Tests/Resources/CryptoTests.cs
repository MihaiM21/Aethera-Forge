using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Infrastructure.Crypto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aethera.Api.Tests.Resources;

public sealed class SecretProtectorTests
{
    private static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();

    private static AesGcmSecretProtector Protector(int current = 1, params (int Version, byte Seed)[] keys)
    {
        var ring = new MasterKeyring(
            (keys.Length == 0 ? [(1, (byte)1)] : keys).ToDictionary(k => k.Version, k => Key(k.Seed)), current);
        return new AesGcmSecretProtector(ring, ownsKeyring: true);
    }

    private const string Aad = "secret:0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55:v1";

    [Fact]
    public void RoundTrip_ReturnsThePlaintext_AndFillsTheEnvelope()
    {
        using var protector = Protector();
        var plaintext = Encoding.UTF8.GetBytes("p@ssw0rd-ünïcode");
        var stored = protector.Protect(plaintext, Aad);

        Assert.Equal(plaintext.Length + 16, stored.Ciphertext.Length);
        Assert.Equal(12, stored.Nonce.Length);
        Assert.Equal(48, stored.WrappedDataKey!.Length);
        Assert.Equal(12, stored.WrappedDataKeyNonce!.Length);
        Assert.Equal(1, stored.KeyVersion);
        Assert.False(stored.Ciphertext.AsSpan().IndexOf("p@ssw0rd"u8) >= 0);
        Assert.Equal(plaintext, protector.Unprotect(stored, Aad));
    }

    [Fact]
    public void EmptyPlaintext_RoundTrips()
    {
        using var protector = Protector();
        Assert.Empty(protector.Unprotect(protector.Protect(ReadOnlySpan<byte>.Empty, Aad), Aad));
    }

    [Fact]
    public void EachEncryption_UsesAFreshNonceAndDataKey()
    {
        using var protector = Protector();
        var a = protector.Protect("same"u8, Aad);
        var b = protector.Protect("same"u8, Aad);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.WrappedDataKey, b.WrappedDataKey);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
    }

    [Fact]
    public void TamperedCiphertext_Fails()
    {
        using var protector = Protector();
        var stored = protector.Protect("value"u8, Aad);
        var tampered = stored with { Ciphertext = Flip(stored.Ciphertext) };
        Assert.Throws<CryptographicException>(() => protector.Unprotect(tampered, Aad));
        var tag = stored with { Ciphertext = Flip(stored.Ciphertext, stored.Ciphertext.Length - 1) };
        Assert.Throws<CryptographicException>(() => protector.Unprotect(tag, Aad));
    }

    [Fact]
    public void TamperedAssociatedData_Fails()
    {
        using var protector = Protector();
        var stored = protector.Protect("value"u8, Aad);
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored, Aad.Replace(":v1", ":v2")));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored, "secret:0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c56:v1"));
    }

    [Fact]
    public void TamperedNonces_AndWrappedKey_Fail()
    {
        using var protector = Protector();
        var stored = protector.Protect("value"u8, Aad);
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored with { Nonce = Flip(stored.Nonce) }, Aad));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored with { WrappedDataKeyNonce = Flip(stored.WrappedDataKeyNonce!) }, Aad));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored with { WrappedDataKey = Flip(stored.WrappedDataKey!) }, Aad));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored with { WrappedDataKey = null }, Aad));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored with { Nonce = [1, 2, 3] }, Aad));
    }

    [Fact]
    public void WrongKeyVersion_Fails()
    {
        // Versions 1 and 2 hold different keys: pointing the value at the other version cannot decrypt it.
        using var protector = Protector(current: 1, (1, 10), (2, 50));
        var stored = protector.Protect("value"u8, Aad);
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored with { KeyVersion = 2 }, Aad));
        // A version that is not in the keyring at all.
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored with { KeyVersion = 9 }, Aad));
    }

    [Fact]
    public void AnotherMasterKey_CannotDecrypt()
    {
        using var first = Protector(1, (1, 10));
        using var second = Protector(1, (1, 77));
        var stored = first.Protect("value"u8, Aad);
        Assert.Throws<CryptographicException>(() => second.Unprotect(stored, Aad));
    }

    [Fact]
    public void KeyRotation_NewValuesUseTheCurrentVersion_OldOnesStayReadable()
    {
        using var v1Only = Protector(1, (1, 10));
        var old = v1Only.Protect("old"u8, Aad);

        using var rotated = Protector(2, (1, 10), (2, 50));
        var fresh = rotated.Protect("new"u8, Aad);
        Assert.Equal(1, old.KeyVersion);
        Assert.Equal(2, fresh.KeyVersion);
        Assert.Equal("old"u8.ToArray(), rotated.Unprotect(old, Aad));
        Assert.Equal("new"u8.ToArray(), rotated.Unprotect(fresh, Aad));
    }

    [Fact]
    public void ErrorsAndLogs_NeverContainKeyMaterialOrPlaintext()
    {
        var keyBytes = Key(33);
        var keyBase64 = Convert.ToBase64String(keyBytes);
        var keyHex = Convert.ToHexString(keyBytes);
        var logger = new CapturingLogger();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [MasterKeyring.MasterKeyConfigKey] = keyBase64 }).Build();

        var messages = new List<string>();
        using (var ring = MasterKeyring.Load(configuration, KeyringMode.Strict, logger))
        {
            using var protector = new AesGcmSecretProtector(ring);
            var stored = protector.Protect("top-secret-plaintext"u8, Aad);
            foreach (var attempt in new Action[]
            {
                () => protector.Unprotect(stored with { Ciphertext = Flip(stored.Ciphertext) }, Aad),
                () => protector.Unprotect(stored, "other"),
                () => protector.Unprotect(stored with { KeyVersion = 7 }, Aad),
            })
            {
                var ex = Assert.ThrowsAny<Exception>(attempt);
                messages.Add(ex.ToString());
            }
        }

        // Malformed and missing configuration also stay silent about the value.
        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [MasterKeyring.MasterKeyConfigKey] = keyBase64 + "AAAA" }).Build();
        messages.Add(Assert.Throws<InvalidOperationException>(() => MasterKeyring.Load(bad, KeyringMode.Strict, logger)).Message);

        foreach (var text in messages.Concat(logger.Messages))
        {
            Assert.DoesNotContain(keyBase64, text);
            Assert.DoesNotContain(keyBase64 + "AAAA", text);
            Assert.DoesNotContain(keyHex, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("top-secret-plaintext", text);
        }
    }

    [Fact]
    public void PlaintextInput_IsNotModifiedAndOutputIsAnIndependentCopy()
    {
        using var protector = Protector();
        var input = "keep-me"u8.ToArray();
        var copy = input.ToArray();
        var stored = protector.Protect(input, Aad);
        Assert.Equal(copy, input);
        var plaintext = protector.Unprotect(stored, Aad);
        CryptographicOperations.ZeroMemory(plaintext); // the caller owns (and clears) the returned buffer
        Assert.Equal("keep-me"u8.ToArray(), protector.Unprotect(stored, Aad));
    }

    private static byte[] Flip(byte[] bytes, int index = 0)
    {
        var copy = bytes.ToArray();
        copy[index] ^= 0x01;
        return copy;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
    }
}

public sealed class MasterKeyringTests
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void SingleKey_IsVersionOne()
    {
        using var ring = MasterKeyring.Load(Config((MasterKeyring.MasterKeyConfigKey, KeyA)), KeyringMode.Strict);
        Assert.Equal(1, ring.CurrentVersion);
        Assert.Equal([1], ring.Versions);
    }

    [Fact]
    public void Keyring_CurrentVersionIsTheHighestUnlessConfigured()
    {
        using var highest = MasterKeyring.Load(Config(($"{MasterKeyring.KeyringConfigSection}:1", KeyA), ($"{MasterKeyring.KeyringConfigSection}:2", KeyB)), KeyringMode.Strict);
        Assert.Equal(2, highest.CurrentVersion);

        using var pinned = MasterKeyring.Load(Config(($"{MasterKeyring.KeyringConfigSection}:1", KeyA), ($"{MasterKeyring.KeyringConfigSection}:2", KeyB),
            (MasterKeyring.CurrentVersionConfigKey, "1")), KeyringMode.Strict);
        Assert.Equal(1, pinned.CurrentVersion);
        Assert.Equal([1, 2], pinned.Versions.Order());
    }

    [Fact]
    public void MissingKey_FailsFastOutsideDevelopmentAndTesting_WithAClearMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MasterKeyring.Load(Config(), KeyringMode.Strict));
        Assert.Contains("AETHERA_MASTER_KEY", error.Message);
        Assert.Contains("openssl rand -base64 32", error.Message);
    }

    [Fact]
    public void Development_GeneratesAnEphemeralKeyAndWarnsLoudly()
    {
        var messages = new List<(LogLevel Level, string Text)>();
        using var first = MasterKeyring.Load(Config(), KeyringMode.Development, new ListLogger(messages));
        using var second = MasterKeyring.Load(Config(), KeyringMode.Development);
        Assert.Contains(messages, m => m.Level == LogLevel.Warning && m.Text.Contains("EPHEMERAL"));
        first.TryGet(1, out var a);
        second.TryGet(1, out var b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Testing_UsesTheFixedKey()
    {
        using var first = MasterKeyring.Load(Config(), KeyringMode.Testing);
        using var second = MasterKeyring.Load(Config(), KeyringMode.Testing);
        first.TryGet(1, out var a);
        second.TryGet(1, out var b);
        Assert.Equal(a, b);
        Assert.Equal(MasterKeyring.TestKey, a);
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")] // valid base64, 3 bytes
    public void MalformedKey_IsRejected_WithoutEchoingIt(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => MasterKeyring.Load(Config((MasterKeyring.MasterKeyConfigKey, value)), KeyringMode.Strict));
        Assert.Contains(MasterKeyring.MasterKeyConfigKey, error.Message);
        Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public void ConflictingDefinitionsOfTheSameVersion_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => MasterKeyring.Load(
            Config((MasterKeyring.MasterKeyConfigKey, KeyA), ($"{MasterKeyring.KeyringConfigSection}:1", KeyB)), KeyringMode.Strict));
    }

    private sealed class ListLogger(List<(LogLevel, string)> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Add((logLevel, formatter(state, exception)));
    }
}
