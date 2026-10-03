using System.Text;
using Aethera.Domain;

namespace Aethera.Api.Features.Resources.Workloads;

/// <summary>Reads and writes <c>.env</c> files: <c>KEY=value</c>, <c>export KEY=value</c>, quotes, comments and multi-line quoted values.</summary>
public static class DotEnv
{
    public sealed record ParseError(int Line, string Message);

    /// <summary>
    /// Parses dotenv text. Later duplicates of a key win. Syntax: blank lines and <c>#</c> comments are skipped; an optional <c>export </c>
    /// prefix is accepted; a value is unquoted (ends at end of line or at whitespace followed by <c>#</c>), <c>'single quoted'</c> (literal)
    /// or <c>"double quoted"</c> (escapes <c>\n \r \t \" \\</c>); quoted values may span lines.
    /// </summary>
    public static (IReadOnlyList<KeyValuePair<string, string>> Values, IReadOnlyList<ParseError> Errors) Parse(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        var errors = new List<ParseError>();
        var text = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var position = 0;
        var line = 1;

        while (position < text.Length)
        {
            var lineStart = line;
            var end = text.IndexOf('\n', position);
            var raw = end < 0 ? text[position..] : text[position..end];
            position = end < 0 ? text.Length : end + 1;
            line++;

            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (trimmed.StartsWith("export ", StringComparison.Ordinal) || trimmed.StartsWith("export\t", StringComparison.Ordinal))
                trimmed = trimmed[7..].TrimStart();

            var equals = trimmed.IndexOf('=');
            if (equals <= 0) { errors.Add(new ParseError(lineStart, "Expected KEY=value.")); continue; }

            var key = trimmed[..equals].TrimEnd();
            if (!EnvironmentVariable.IsValidKey(key)) { errors.Add(new ParseError(lineStart, "The name is not a valid variable name.")); continue; }
            if (key.Length > 255) { errors.Add(new ParseError(lineStart, "The name is too long.")); continue; }

            var rest = trimmed[(equals + 1)..].TrimStart();
            string value;
            if (rest.StartsWith('"') || rest.StartsWith('\''))
            {
                var quote = rest[0];
                var builder = new StringBuilder();
                var current = rest[1..];
                var closed = false;
                string after = "";
                while (true)
                {
                    var i = 0;
                    for (; i < current.Length; i++)
                    {
                        var c = current[i];
                        if (quote == '"' && c == '\\' && i + 1 < current.Length)
                        {
                            i++;
                            builder.Append(current[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', var other => other });
                            if (current[i] is not ('n' or 'r' or 't' or '"' or '\\')) builder.Insert(builder.Length - 1, '\\');
                        }
                        else if (c == quote)
                        {
                            closed = true;
                            after = current[(i + 1)..];
                            break;
                        }
                        else
                        {
                            builder.Append(c);
                        }
                    }

                    if (closed || position >= text.Length) break;

                    // The value continues on the next line.
                    builder.Append('\n');
                    var next = text.IndexOf('\n', position);
                    current = next < 0 ? text[position..] : text[position..next];
                    position = next < 0 ? text.Length : next + 1;
                    line++;
                }

                if (!closed) { errors.Add(new ParseError(lineStart, "The quoted value is not closed.")); continue; }
                var tail = after.Trim();
                if (tail.Length > 0 && tail[0] != '#') { errors.Add(new ParseError(lineStart, "Unexpected text after the closing quote.")); continue; }
                value = builder.ToString();
            }
            else
            {
                var hash = -1;
                for (var i = 0; i < rest.Length; i++)
                {
                    if (rest[i] == '#' && (i == 0 || char.IsWhiteSpace(rest[i - 1]))) { hash = i; break; }
                }

                value = (hash < 0 ? rest : rest[..hash]).TrimEnd();
            }

            if (!values.ContainsKey(key)) order.Add(key);
            values[key] = value;
        }

        return (order.Select(k => new KeyValuePair<string, string>(k, values[k])).ToList(), errors);
    }

    /// <summary>Formats <c>KEY=value</c> lines, quoting values that would not survive <see cref="Parse"/> unquoted.</summary>
    public static string Format(IEnumerable<KeyValuePair<string, string>> values)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in values)
        {
            builder.Append(key).Append('=').Append(NeedsQuotes(value) ? Quote(value) : value).Append('\n');
        }

        return builder.ToString();
    }

    private static bool NeedsQuotes(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])
            || value.AsSpan().IndexOfAny("\"'\\#\n\r\t") >= 0 || value[0] == '$');

    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        }

        return builder.Append('"').ToString();
    }
}
