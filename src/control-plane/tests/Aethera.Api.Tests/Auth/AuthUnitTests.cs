using System.Security.Cryptography;
using System.Text;
using Aethera.Api.Features.Auth;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Auth;
using Microsoft.AspNetCore.Identity;

namespace Aethera.Api.Tests.Auth;

public sealed class ApiTokenFormatTests
{
    [Fact]
    public void Generate_ProducesTheDocumentedFormat()
    {
        var token = ApiTokenFormat.Generate();

        Assert.Equal(45, token.Plaintext.Length);
        Assert.StartsWith("aeth_", token.Plaintext);
        Assert.All(token.Plaintext[5..], c => Assert.True(char.IsAsciiLetterOrDigit(c), $"'{c}' is not base62"));
        Assert.Equal(8, token.Prefix.Length);
        Assert.Equal(token.Plaintext[5..13], token.Prefix);
        Assert.Equal(32, token.SecretHash.Length);
    }

    [Fact]
    public void Generate_IsRandom()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => ApiTokenFormat.Generate().Plaintext).ToList();
        Assert.Equal(tokens.Count, tokens.Distinct().Count());

        // Every one of the 62 characters is drawn (400 x 40 draws make a missing one astronomically unlikely) and none is outside it.
        var seen = Enumerable.Range(0, 400).SelectMany(_ => ApiTokenFormat.Generate().Plaintext[5..]).ToHashSet();
        Assert.Equal(62, seen.Count);
    }

    [Fact]
    public void Hash_IsSha256OfTheSecretPart_NotOfThePlaintext()
    {
        var token = ApiTokenFormat.Generate();
        var secret = token.Plaintext[5..];
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(secret)), token.SecretHash);
        Assert.NotEqual(SHA256.HashData(Encoding.ASCII.GetBytes(token.Plaintext)), token.SecretHash);
        Assert.True(ApiTokenFormat.Matches(token.SecretHash, secret));
        Assert.False(ApiTokenFormat.Matches(token.SecretHash, secret[..^1] + (secret[^1] == 'a' ? 'b' : 'a')));
    }

    [Fact]
    public void TryParse_AcceptsGeneratedTokens_AndSplitsPrefixFromSecret()
    {
        var token = ApiTokenFormat.Generate();
        Assert.True(ApiTokenFormat.TryParse(token.Plaintext, out var parsed));
        Assert.Equal(token.Prefix, parsed.Prefix);
        Assert.Equal(token.Plaintext[5..], parsed.Secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aeth_")]
    [InlineData("aeth_short")]
    [InlineData("AETH_0123456789012345678901234567890123456789")] // marker is case-sensitive
    [InlineData("tok__0123456789012345678901234567890123456789")]
    [InlineData("aeth_012345678901234567890123456789012345678")] // 39 characters
    [InlineData("aeth_01234567890123456789012345678901234567890")] // 41 characters
    [InlineData("aeth_0123456789012345678901234567890123456-89")] // not base62
    [InlineData("aeth_0123456789012345678901234567890123456 89")]
    [InlineData("aeth_0123456789012345678901234567890123456é89")]
    public void TryParse_RejectsAnythingElse(string? value)
    {
        Assert.False(ApiTokenFormat.TryParse(value, out _));
    }
}

public sealed class SessionSecretTests
{
    [Theory]
    [InlineData(false, 's')]
    [InlineData(true, 'p')]
    public void Generate_Makes256BitsOfBase64Url_WithTheLifetimeMarker(bool persistent, char marker)
    {
        var secret = SessionSecret.Generate(persistent);

        Assert.Equal(SessionSecret.CookieValueLength, secret.CookieValue.Length);
        Assert.Equal(marker, secret.CookieValue[0]);
        Assert.Equal(32, Convert.FromBase64String(Pad(secret.CookieValue[1..].Replace('-', '+').Replace('_', '/'))).Length);
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(secret.CookieValue)), secret.Hash);
        Assert.Equal(persistent, secret.Persistent);
    }

    [Fact]
    public void TryParse_RoundTrips_AndTheMarkerIsBoundToTheHash()
    {
        var persistent = SessionSecret.Generate(persistent: true);
        Assert.True(SessionSecret.TryParse(persistent.CookieValue, out var parsed));
        Assert.True(parsed.Persistent);
        Assert.Equal(persistent.Hash, parsed.Hash);

        // Flipping the marker of a stolen cookie does not turn it into another valid session: the hash changes.
        var flipped = "s" + persistent.CookieValue[1..];
        Assert.True(SessionSecret.TryParse(flipped, out var other));
        Assert.NotEqual(persistent.Hash, other.Hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s")]
    [InlineData("xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // wrong lifetime marker
    [InlineData("sAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // one character short
    [InlineData("sAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // one character long
    [InlineData("sAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+A")] // right length, not base64url
    [InlineData("sAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=A")]
    public void TryParse_RejectsMalformedValues(string? value)
    {
        Assert.False(SessionSecret.TryParse(value, out _));
    }

    [Fact]
    public void CsrfToken_IsDeterministicPerSession_AndDifferentBetweenSessions()
    {
        var a = SessionSecret.Generate(false);
        var b = SessionSecret.Generate(false);

        Assert.Equal(SessionSecret.CsrfToken(a.Hash), SessionSecret.CsrfToken(a.Hash));
        Assert.NotEqual(SessionSecret.CsrfToken(a.Hash), SessionSecret.CsrfToken(b.Hash));
        Assert.Equal(43, SessionSecret.CsrfToken(a.Hash).Length);
        // Not derivable from what the cookie holds in the clear.
        Assert.NotEqual(Convert.ToBase64String(a.Hash), SessionSecret.CsrfToken(a.Hash));
    }

    [Fact]
    public void IsValidCsrfToken_ComparesTheWholeToken()
    {
        var session = SessionSecret.Generate(false);
        var token = SessionSecret.CsrfToken(session.Hash);

        Assert.True(SessionSecret.IsValidCsrfToken(session.Hash, token));
        Assert.False(SessionSecret.IsValidCsrfToken(session.Hash, token[..^1]));
        Assert.False(SessionSecret.IsValidCsrfToken(session.Hash, token + "A"));
        Assert.False(SessionSecret.IsValidCsrfToken(session.Hash, ""));
        Assert.False(SessionSecret.IsValidCsrfToken(session.Hash, null));
        Assert.False(SessionSecret.IsValidCsrfToken(SessionSecret.Generate(false).Hash, token));
        Assert.False(SessionSecret.IsValidCsrfToken(session.Hash, new string('A', 5000)));
    }

    private static string Pad(string base64) => base64 + new string('=', (4 - base64.Length % 4) % 4);
}

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("", PasswordProblem.TooShort)]
    [InlineData(null, PasswordProblem.TooShort)]
    [InlineData("12345678901", PasswordProblem.TooShort)]
    public void TooShort(string? password, PasswordProblem expected) =>
        Assert.Equal(expected, PasswordPolicy.Check(password, "a@example.com"));

    [Fact]
    public void Boundaries_Are12And256Characters()
    {
        Assert.Null(PasswordPolicy.Check(new string('x', 12), "a@example.com"));
        Assert.Null(PasswordPolicy.Check(new string('x', 256), "a@example.com"));
        Assert.Equal(PasswordProblem.TooLong, PasswordPolicy.Check(new string('x', 257), "a@example.com"));
        Assert.Equal(PasswordProblem.TooShort, PasswordPolicy.Check(new string('x', 11), "a@example.com"));
    }

    [Theory]
    [InlineData("someone.long@example.com", "someone.long@example.com")]
    [InlineData("SOMEONE.LONG@EXAMPLE.COM", "someone.long@example.com")]
    [InlineData("  someone.long@example.com ", "someone.long@example.com")]
    public void MayNotBeTheEmailAddress(string password, string email) =>
        Assert.Equal(PasswordProblem.SameAsEmail, PasswordPolicy.Check(password, email));

    [Fact]
    public void WithoutAnEmail_OnlyLengthMatters() =>
        Assert.Null(PasswordPolicy.Check("someone.long@example.com", null));

    [Fact]
    public void Hashing_UsesTheIdentityHasher_AndRoundTrips()
    {
        var service = new PasswordService();
        var user = new User("a@example.com", "A");
        var hash = service.Hash(user, "correct horse battery staple");
        user.PasswordHash = hash;

        Assert.NotEqual("correct horse battery staple", hash);
        Assert.NotEqual(hash, service.Hash(user, "correct horse battery staple")); // salted
        Assert.Equal(PasswordCheck.Success, service.Verify(user, "correct horse battery staple"));
        Assert.Equal(PasswordCheck.Failed, service.Verify(user, "wrong horse battery staple"));
        Assert.Equal(PasswordCheck.Failed, service.Verify(null, "correct horse battery staple")); // unknown user
        Assert.Equal(PasswordCheck.Failed, service.Verify(new User("b@example.com", "B"), "anything at all 123")); // no password set
    }

    [Fact]
    public void AnOutdatedHash_IsAcceptedButFlaggedForRehash()
    {
        var legacy = new PasswordHasher<User>(new Microsoft.Extensions.Options.OptionsWrapper<PasswordHasherOptions>(
            new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
        var user = new User("a@example.com", "A") { PasswordHash = legacy.HashPassword(null!, "correct horse battery staple") };

        Assert.Equal(PasswordCheck.SuccessRehashNeeded, new PasswordService().Verify(user, "correct horse battery staple"));
        Assert.Equal(PasswordCheck.Failed, new PasswordService().Verify(user, "another password entirely"));
    }
}

public sealed class ScopeCeilingTests
{
    [Fact]
    public void EachRoleHasItsDocumentedCeiling()
    {
        Assert.Equal([Scopes.Read], ScopeCeiling.AllowedFor(OrganizationRole.Viewer));
        Assert.Equal([Scopes.Read, Scopes.Write, Scopes.Deploy], ScopeCeiling.AllowedFor(OrganizationRole.Developer));
        foreach (var role in new[] { OrganizationRole.Admin, OrganizationRole.Owner })
        {
            var allowed = ScopeCeiling.AllowedFor(role);
            Assert.All(Scopes.Known, scope => Assert.Contains(scope, allowed));
            Assert.Contains(Scopes.All, allowed);
        }
    }

    [Fact]
    public void TheCeilingGrowsWithTheRole()
    {
        var roles = new[] { OrganizationRole.Viewer, OrganizationRole.Developer, OrganizationRole.Admin, OrganizationRole.Owner };
        for (var i = 1; i < roles.Length; i++)
            Assert.All(ScopeCeiling.AllowedFor(roles[i - 1]), scope => Assert.True(ScopeCeiling.IsAllowed(roles[i], scope)));
    }

    [Fact]
    public void Effective_DropsWhatTheRoleNoLongerAllows_AndExpandsTheWildcard()
    {
        Assert.Equal([Scopes.Read], ScopeCeiling.Effective(OrganizationRole.Viewer, [Scopes.Read, Scopes.Write, Scopes.Admin]));
        Assert.Empty(ScopeCeiling.Effective(OrganizationRole.Viewer, [Scopes.Write]));
        Assert.Equal([Scopes.Read, Scopes.Write, Scopes.Deploy], ScopeCeiling.Effective(OrganizationRole.Developer, [Scopes.All]));
        Assert.Equal([Scopes.All, Scopes.Admin], ScopeCeiling.Effective(OrganizationRole.Admin, [Scopes.All, Scopes.Admin, Scopes.All]));
    }
}
