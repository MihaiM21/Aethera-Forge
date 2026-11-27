using System.Text;
using Aethera.Infrastructure.Auth;
using Aethera.Infrastructure.Crypto;

namespace Aethera.Api.Tests.Auth;

/// <summary>TOTP, base32, recovery codes and the sign-in challenge, without a database (ADR 0013).</summary>
public sealed class TwoFactorUnitTests
{
    // RFC 6238 appendix B: the SHA-1 secret is the ASCII "12345678901234567890"; the 8-digit codes there end in these 6 digits.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Code_MatchesTheRfc6238TestVectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expected, Totp.Code(RfcSecret, step));
    }

    [Fact]
    public void TryMatch_AcceptsOneStepOfDriftEitherSide_AndNothingFurther()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);

        Assert.True(Totp.TryMatch(RfcSecret, Totp.Code(RfcSecret, step), now, null, out var matched));
        Assert.Equal(step, matched);
        Assert.True(Totp.TryMatch(RfcSecret, Totp.Code(RfcSecret, step - 1), now, null, out _));
        Assert.True(Totp.TryMatch(RfcSecret, Totp.Code(RfcSecret, step + 1), now, null, out _));
        Assert.False(Totp.TryMatch(RfcSecret, Totp.Code(RfcSecret, step - 2), now, null, out _));
        Assert.False(Totp.TryMatch(RfcSecret, Totp.Code(RfcSecret, step + 2), now, null, out _));
    }

    [Fact]
    public void TryMatch_RefusesTheLastUsedStepAndEarlierOnes()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.Code(RfcSecret, step);

        Assert.False(Totp.TryMatch(RfcSecret, code, now, lastUsedStep: step, out _));
        Assert.False(Totp.TryMatch(RfcSecret, Totp.Code(RfcSecret, step - 1), now, lastUsedStep: step, out _));
        Assert.True(Totp.TryMatch(RfcSecret, Totp.Code(RfcSecret, step + 1), now, lastUsedStep: step, out _));
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData("123 456", "123456")]
    [InlineData(" 123-456 ", "123456")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12345a", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_StripsSpacesAndDashes_AndInsistsOnSixDigits(string? input, string? expected) =>
        Assert.Equal(expected, Totp.Normalize(input));

    [Fact]
    public void ProvisioningUri_IsTheKeyUriFormat()
    {
        var uri = Totp.ProvisioningUri("Aethera (prod)", "me@example.com", RfcSecret);
        Assert.Equal(
            "otpauth://totp/Aethera%20%28prod%29:me%40example.com?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Aethera%20%28prod%29" +
            "&algorithm=SHA1&digits=6&period=30", uri);
    }

    [Fact]
    public void Base32_RoundTrips_AndMatchesRfc4648()
    {
        Assert.Equal("MZXW6YTBOI", Base32.Encode("foobar"u8));
        Assert.Equal("foobar"u8.ToArray(), Base32.Decode("mzxw 6ytb oi======"));
        var random = Totp.GenerateSecret();
        Assert.Equal(random, Base32.Decode(Base32.Encode(random)));
        Assert.Throws<FormatException>(() => Base32.Decode("not*base32"));
    }

    [Fact]
    public void RecoveryCodes_AreTenDistinctEightyBitCodes_ThatNormalizeAndHashStably()
    {
        var codes = RecoveryCodes.Generate();
        Assert.Equal(RecoveryCodes.Count, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        foreach (var code in codes)
        {
            Assert.Matches("^[a-z2-7]{4}-[a-z2-7]{4}-[a-z2-7]{4}-[a-z2-7]{4}$", code);
            var normalized = RecoveryCodes.Normalize(code.ToUpperInvariant().Replace("-", " "));
            Assert.Equal(code.Replace("-", ""), normalized);
            Assert.Equal(32, RecoveryCodes.Hash(normalized!).Length);
        }

        Assert.Null(RecoveryCodes.Normalize("abcd-efgh-ijkl"));
        Assert.Null(RecoveryCodes.Normalize("abcd-efgh-ijkl-mno1")); // 1 is not base32
        Assert.Null(RecoveryCodes.Normalize(null));
    }

    private static MasterKeyring Keyring(params (int Version, byte Fill)[] keys) =>
        new(keys.ToDictionary(k => k.Version, k => Enumerable.Repeat(k.Fill, MasterKeyring.KeyLength).ToArray()), keys[^1].Version);

    [Fact]
    public void Challenge_RoundTrips_WithTheUserAndRememberMe()
    {
        using var keyring = Keyring((1, 7));
        var challenge = new TwoFactorChallenge(keyring);
        var user = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var token = challenge.Issue(user, "hash-1", rememberMe: true, now);
        var ticket = challenge.Read(token, now.AddMinutes(4), out var stamp);

        Assert.NotNull(ticket);
        Assert.Equal(user, ticket.UserId);
        Assert.True(ticket.RememberMe);
        Assert.True(TwoFactorChallenge.MatchesPassword(stamp, "hash-1"));
        Assert.False(TwoFactorChallenge.MatchesPassword(stamp, "hash-2"));
        Assert.DoesNotContain(user.ToString("N"), token); // not readable as text, though not secret either
    }

    [Fact]
    public void Challenge_Expires_AfterFiveMinutes()
    {
        using var keyring = Keyring((1, 7));
        var challenge = new TwoFactorChallenge(keyring);
        var now = DateTimeOffset.UtcNow;
        var token = challenge.Issue(Guid.CreateVersion7(), "h", false, now);

        Assert.Null(challenge.Read(token, now + TwoFactorChallenge.Lifetime + TimeSpan.FromSeconds(1), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("abc.def")]
    [InlineData("....")]
    public void Challenge_RejectsGarbage(string? token)
    {
        using var keyring = Keyring((1, 7));
        Assert.Null(new TwoFactorChallenge(keyring).Read(token, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void Challenge_RejectsATamperedPayload_AndAnotherInstallationsKey()
    {
        using var keyring = Keyring((1, 7));
        using var other = Keyring((1, 9));
        var now = DateTimeOffset.UtcNow;
        var token = new TwoFactorChallenge(keyring).Issue(Guid.CreateVersion7(), "h", false, now);

        var dot = token.IndexOf('.');
        var payload = token[..dot].ToCharArray();
        payload[3] = payload[3] == 'A' ? 'B' : 'A';
        Assert.Null(new TwoFactorChallenge(keyring).Read(new string(payload) + token[dot..], now, out _));
        Assert.Null(new TwoFactorChallenge(other).Read(token, now, out _));
    }

    [Fact]
    public void Challenge_StaysReadableAfterAKeyRotation_WhileTheOldKeyIsKept()
    {
        using var before = Keyring((1, 7));
        using var after = Keyring((1, 7), (2, 8));
        var now = DateTimeOffset.UtcNow;
        var token = new TwoFactorChallenge(before).Issue(Guid.CreateVersion7(), "h", false, now);

        Assert.NotNull(new TwoFactorChallenge(after).Read(token, now, out _));
    }
}
