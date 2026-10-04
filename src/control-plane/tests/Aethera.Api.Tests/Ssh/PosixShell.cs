using System.Text;

namespace Aethera.Api.Tests.Ssh;

/// <summary>
/// An independent, deliberately strict reader of the command lines the SSH transport produces. It understands only what the transport is
/// allowed to emit (bare words made of safe characters, single-quoted words, <c>&amp;&amp;</c>) and refuses anything else, so a test that
/// tokenizes a line proves that no shell metacharacter reaches the shell unquoted.
/// </summary>
public static class PosixShell
{
    private const string SafeBare = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-./:=,@%+";

    /// <summary>Splits <paramref name="line"/> into words the way <c>sh</c> would. The operator <c>&amp;&amp;</c> comes back as its own token.</summary>
    /// <exception cref="FormatException">An unquoted shell metacharacter, a double quote, a backslash or an unterminated quote.</exception>
    public static List<string> Split(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'')
            {
                inWord = true;
                var end = line.IndexOf('\'', i + 1);
                if (end < 0) throw new FormatException("Unterminated single quote.");
                current.Append(line, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '\\' && i + 1 < line.Length && line[i + 1] == '\'')
            {
                inWord = true; // the only escape the quoter emits: a backslash-quote between two single-quoted runs
                current.Append('\'');
                i++;
            }
            else if (c == ' ')
            {
                if (inWord) { tokens.Add(current.ToString()); current.Clear(); inWord = false; }
            }
            else if (c == '&' && i + 1 < line.Length && line[i + 1] == '&' && !inWord)
            {
                tokens.Add("&&");
                i++;
            }
            else if (SafeBare.Contains(c))
            {
                inWord = true;
                current.Append(c);
            }
            else
            {
                throw new FormatException($"Unquoted metacharacter '{c}' at {i}.");
            }
        }

        if (inWord) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Strings that exercise every quoting hazard of a POSIX shell.</summary>
    public static IEnumerable<string> HostileStrings()
    {
        yield return "";
        yield return " ";
        yield return "a b";
        yield return "'";
        yield return "''";
        yield return "'; rm -rf / ; '";
        yield return "\"; reboot; \"";
        yield return "$(reboot)";
        yield return "`reboot`";
        yield return "${IFS}";
        yield return "a;b";
        yield return "a|b";
        yield return "a&&b";
        yield return "a&b";
        yield return "a > /etc/passwd";
        yield return "a < /dev/null";
        yield return "line1\nline2";
        yield return "tab\there";
        yield return "back\\slash";
        yield return "-rf";
        yield return "--privileged";
        yield return "*";
        yield return "~root";
        yield return "!history";
        yield return "#comment";
        yield return "{a,b}";
        yield return "ünïcödé ✓ 日本語";
        yield return "%s %n";
        yield return "end'";
        yield return "'start";
    }

    /// <summary>Pseudo-random hostile strings (seeded, so a failure reproduces).</summary>
    public static IEnumerable<string> RandomHostileStrings(int count, int seed = 1234)
    {
        var alphabet = "'\"\\$`;|&<>()*?[]{}~!#% \t\nab-_./:=" + "\u00fc\u00ef\u2713\u00a0" + (char)0x2028;
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var length = random.Next(0, 40);
            var chars = new char[length];
            for (var j = 0; j < length; j++) chars[j] = alphabet[random.Next(alphabet.Length)];
            yield return new string(chars);
        }
    }
}
