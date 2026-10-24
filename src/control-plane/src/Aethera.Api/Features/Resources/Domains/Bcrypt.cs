using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Aethera.Api.Features.Resources.DomainNames;

/// <summary>
/// bcrypt (the OpenBSD algorithm, <c>$2y$</c> modular-crypt strings) for the basic-auth users of a domain, which Traefik reads from htpasswd
/// lines. The package set is fixed and has no bcrypt, so this is a plain implementation of the published algorithm; it is checked against the
/// reference test vectors of crypt_blowfish and against Go's <c>x/crypto/bcrypt</c> in the tests. Only used to <em>hash</em> new passwords: there
/// is no login through it.
/// </summary>
public static class Bcrypt
{
    public const int DefaultCost = 10;
    private const string Alphabet = "./ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly byte[] Magic = "OrpheanBeholderScryDoubt"u8.ToArray();

    // Blowfish starts from the fractional hexadecimal digits of pi: P (18 words) then four S boxes (256 words each). Computed once, exactly,
    // with Machin's formula instead of embedding 4 KiB of constants that cannot be reviewed by eye.
    private static readonly Lazy<uint[]> Pi = new(ComputePi);

    private static uint[] ComputePi()
    {
        const int words = 18 + 4 * 256;
        const int bits = words * 32 + 64; // 64 guard bits
        var scale = BigInteger.One << bits;
        var pi = 16 * ArcTanInverse(5, scale) - 4 * ArcTanInverse(239, scale);
        var fraction = pi - (3 * scale);
        var result = new uint[words];
        for (var i = 0; i < words; i++)
            result[i] = (uint)((fraction >> (bits - 32 * (i + 1))) & 0xFFFFFFFF);
        return result;
    }

    private static BigInteger ArcTanInverse(int x, BigInteger scale)
    {
        var square = (BigInteger)x * x;
        var term = scale / x;
        var sum = term;
        for (var k = 1; !term.IsZero; k++)
        {
            term /= square;
            sum += (k % 2 == 0 ? 1 : -1) * (term / (2 * k + 1));
        }

        return sum;
    }

    /// <summary>A <c>$2y$</c> hash of <paramref name="password"/> (at most 72 bytes are significant, as in every bcrypt).</summary>
    public static string Hash(string password, int cost = DefaultCost)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Format("2y", cost, salt, Compute(Encoding.UTF8.GetBytes(password), salt, cost));
    }

    /// <summary>Whether <paramref name="password"/> matches a <c>$2a$</c>, <c>$2b$</c> or <c>$2y$</c> hash.</summary>
    public static bool Verify(string password, string hash)
    {
        if (hash.Length != 60 || hash[0] != '$' || hash[1] != '2' || hash[3] != '$' || hash[6] != '$') return false;
        if (!int.TryParse(hash.AsSpan(4, 2), out var cost) || cost is < 4 or > 31) return false;
        var salt = Decode(hash.Substring(7, 22), 16);
        if (salt is null) return false;
        var expected = Format(hash.Substring(1, 2), cost, salt, Compute(Encoding.UTF8.GetBytes(password), salt, cost));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(hash));
    }

    private static string Format(string version, int cost, byte[] salt, byte[] digest) =>
        $"${version}${cost:00}${Encode(salt, 16)}{Encode(digest, 23)}";

    private static byte[] Compute(byte[] password, byte[] salt, int cost)
    {
        if (cost is < 4 or > 31) throw new ArgumentOutOfRangeException(nameof(cost));
        var key = new byte[Math.Min(password.Length + 1, 72)]; // the terminating NUL counts
        Array.Copy(password, key, Math.Min(password.Length, key.Length));

        var state = new State();
        state.ExpandKey(salt, key);
        for (long i = 0; i < 1L << cost; i++)
        {
            state.ExpandKey(null, key);
            state.ExpandKey(null, salt);
        }

        var text = new uint[6];
        for (var i = 0; i < 6; i++) text[i] = (uint)(Magic[4 * i] << 24 | Magic[4 * i + 1] << 16 | Magic[4 * i + 2] << 8 | Magic[4 * i + 3]);
        for (var round = 0; round < 64; round++)
            for (var i = 0; i < 6; i += 2) state.Encrypt(ref text[i], ref text[i + 1]);

        var digest = new byte[24];
        for (var i = 0; i < 6; i++)
        {
            digest[4 * i] = (byte)(text[i] >> 24);
            digest[4 * i + 1] = (byte)(text[i] >> 16);
            digest[4 * i + 2] = (byte)(text[i] >> 8);
            digest[4 * i + 3] = (byte)text[i];
        }

        return digest;
    }

    private static string Encode(byte[] data, int length)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < length)
        {
            var c1 = data[i++];
            sb.Append(Alphabet[c1 >> 2]);
            var c = (c1 & 0x03) << 4;
            if (i >= length) { sb.Append(Alphabet[c]); break; }
            var c2 = data[i++];
            c |= c2 >> 4;
            sb.Append(Alphabet[c]);
            c = (c2 & 0x0f) << 2;
            if (i >= length) { sb.Append(Alphabet[c]); break; }
            var c3 = data[i++];
            c |= c3 >> 6;
            sb.Append(Alphabet[c]);
            sb.Append(Alphabet[c3 & 0x3f]);
        }

        return sb.ToString();
    }

    private static byte[]? Decode(string text, int length)
    {
        var result = new byte[length];
        var position = 0;
        var offset = 0;
        while (position < length)
        {
            var c = new int[4];
            for (var k = 0; k < 4; k++)
            {
                if (offset + k >= text.Length) { c[k] = k < 2 ? -1 : 0; if (k < 2) return null; continue; }
                c[k] = Alphabet.IndexOf(text[offset + k]);
                if (c[k] < 0) return null;
            }

            offset += 4;
            result[position++] = (byte)(c[0] << 2 | c[1] >> 4);
            if (position >= length) break;
            result[position++] = (byte)((c[1] & 0x0f) << 4 | c[2] >> 2);
            if (position >= length) break;
            result[position++] = (byte)((c[2] & 0x03) << 6 | c[3]);
        }

        return result;
    }

    private sealed class State
    {
        private readonly uint[] _p = new uint[18];
        private readonly uint[] _s = new uint[4 * 256];

        public State()
        {
            var pi = Pi.Value;
            Array.Copy(pi, 0, _p, 0, 18);
            Array.Copy(pi, 18, _s, 0, 4 * 256);
        }

        private static uint Word(byte[] data, ref int position)
        {
            uint word = 0;
            for (var i = 0; i < 4; i++)
            {
                word = word << 8 | data[position];
                position = (position + 1) % data.Length;
            }

            return word;
        }

        public void ExpandKey(byte[]? salt, byte[] key)
        {
            var j = 0;
            for (var i = 0; i < 18; i++) _p[i] ^= Word(key, ref j);

            uint l = 0, r = 0;
            var saltPosition = 0;
            for (var i = 0; i < 18; i += 2)
            {
                if (salt is not null) { l ^= Word(salt, ref saltPosition); r ^= Word(salt, ref saltPosition); }
                Encrypt(ref l, ref r);
                _p[i] = l;
                _p[i + 1] = r;
            }

            for (var i = 0; i < _s.Length; i += 2)
            {
                if (salt is not null) { l ^= Word(salt, ref saltPosition); r ^= Word(salt, ref saltPosition); }
                Encrypt(ref l, ref r);
                _s[i] = l;
                _s[i + 1] = r;
            }
        }

        private uint F(uint x) => ((_s[x >> 24] + _s[256 + (byte)(x >> 16)]) ^ _s[512 + (byte)(x >> 8)]) + _s[768 + (byte)x];

        public void Encrypt(ref uint left, ref uint right)
        {
            var xl = left ^ _p[0];
            var xr = right;
            for (var n = 1; n <= 16; n += 2)
            {
                xr ^= F(xl) ^ _p[n];
                xl ^= F(xr) ^ _p[n + 1];
            }

            left = xr ^ _p[17];
            right = xl;
        }
    }
}
