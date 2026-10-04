using System.Text;
using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Ssh.Docker;

/// <summary>
/// POSIX shell quoting for the one string an SSH <c>exec</c> request carries. Every variable token of every command goes through
/// <see cref="Quote"/>; the only things that are ever appended to a command line verbatim are the literal words of the fixed templates in
/// <see cref="DockerCommands"/> (ADR 0002 "SshTransport": no caller-supplied string is interpreted as a command).
/// </summary>
public static class ShellQuote
{
    /// <summary>Characters that never need quoting in a POSIX shell word.</summary>
    private static bool IsSafe(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or '/' or ':' or '=' or ',' or '@' or '%' or '+';

    /// <summary>
    /// Quotes <paramref name="value"/> as one shell word: single quotes around everything, each embedded <c>'</c> written as <c>'\''</c>.
    /// Newlines, <c>$</c>, backticks, <c>;</c>, <c>|</c>, <c>&amp;</c>, globs and Unicode all stay literal inside single quotes.
    /// </summary>
    /// <exception cref="ServerTransportException">The value contains a NUL character, which no shell can carry.</exception>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ServerTransportException(TransportErrors.CommandRejected, "A value contains a NUL character.");
        if (value.Length > 0 && value.All(IsSafe) && value[0] != '-') return value;

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (var c in value)
        {
            if (c == '\'') builder.Append("'\\''");
            else builder.Append(c);
        }

        return builder.Append('\'').ToString();
    }

    /// <summary>Joins already-tokenized words into one command line, quoting each.</summary>
    public static string Join(IEnumerable<string> argv) => string.Join(' ', argv.Select(Quote));
}
