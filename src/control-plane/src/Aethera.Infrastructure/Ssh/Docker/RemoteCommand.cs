using System.Text;
using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Ssh.Docker;

/// <summary>
/// One SSH <c>exec</c> request: the command line, the bytes written to its standard input, and the secret values that travel in that
/// input (so every output line can be masked). The command line never contains a secret by construction (ADR 0002).
/// </summary>
public sealed record RemoteCommand(string Line, string? Stdin = null, IReadOnlyList<string>? Secrets = null)
{
    /// <summary>Safe to log: the command line carries no secrets and no stdin.</summary>
    public override string ToString() => Line;
}

/// <summary>
/// Builds a shell command from literal words (<see cref="Lit"/>, only ever compile-time constants of the fixed templates) and data
/// (<see cref="Arg"/>, always quoted).
/// </summary>
public sealed class Cmd
{
    private readonly List<string> _words = [];

    public Cmd(string program) => Lit(program);

    /// <summary>A literal word of the template. Never call with caller-supplied text.</summary>
    public Cmd Lit(string word)
    {
        _words.Add(word);
        return this;
    }

    /// <summary>A data word: quoted.</summary>
    public Cmd Arg(string value)
    {
        _words.Add(ShellQuote.Quote(value));
        return this;
    }

    /// <summary>A literal option followed by a quoted value: <c>--name 'x'</c>.</summary>
    public Cmd Opt(string option, string value) => Lit(option).Arg(value);

    public Cmd If(bool condition, string option) => condition ? Lit(option) : this;

    public Cmd OptIf(bool condition, string option, Func<string> value) => condition ? Opt(option, value()) : this;

    public override string ToString() => string.Join(' ', _words);

    public RemoteCommand ToRemote() => new(ToString());
}

/// <summary>
/// Wraps a command in a small fixed <c>sh</c> script when it needs secrets: registry logins read their passwords from standard input into a
/// throw-away <c>DOCKER_CONFIG</c> (so nothing is written to the user's Docker config), and an env-file or build-arg file is written
/// <c>0600</c> into a private temp directory that is removed when the script ends (ADR 0002 "Secrets never appear in argv").
/// Standard input layout: one password line per login, then the file content.
/// </summary>
public sealed class SecretScript
{
    private readonly List<(string Server, string User, string Password)> _logins = [];
    private string? _fileContent;
    private bool _hasFile;
    private readonly List<string> _secrets = [];

    /// <summary>Path of the temp file inside the script (<c>"$d/in"</c>), for use as a raw word.</summary>
    public const string FileWord = "\"$d/in\"";

    public SecretScript Login(RegistryCredentials credentials)
    {
        if (credentials.IdentityToken is not null && credentials.Password is null)
            throw new ServerTransportException(TransportErrors.Unsupported, "Registry identity tokens are not supported over SSH; use a password. Enable the agent on this server for token logins.");
        var password = credentials.Password?.Value ?? throw new ServerTransportException(TransportErrors.CommandRejected, "A registry credential needs a password.");
        _logins.Add((SshValidators.RegistryServer(credentials.Server), SshValidators.OneLine(credentials.Username, "The registry user name"), SshValidators.OneLine(password, "The registry password")));
        _secrets.Add(password);
        return this;
    }

    /// <summary>The content written to the private temp file the body can reference as <see cref="FileWord"/>.</summary>
    public SecretScript WithFile(string content, IEnumerable<string> secretValues)
    {
        _hasFile = true;
        _fileContent = content;
        _secrets.AddRange(secretValues.Where(v => v.Length > 0));
        return this;
    }

    public bool NeedsScript => _logins.Count > 0 || _hasFile;

    /// <summary>Builds the exec request for <paramref name="body"/> (a command line that may reference <see cref="FileWord"/>).</summary>
    public RemoteCommand Build(string body)
    {
        if (!NeedsScript) return new RemoteCommand(body);

        var script = new StringBuilder();
        script.Append("set -eu\numask 077\nd=$(mktemp -d)\ntrap 'rm -rf \"$d\"' EXIT\n");
        if (_logins.Count > 0)
        {
            script.Append("export DOCKER_CONFIG=\"$d/dc\"\nmkdir \"$DOCKER_CONFIG\"\n");
            script.Append("if [ -d \"${HOME:-/nonexistent}/.docker/cli-plugins\" ]; then ln -s \"$HOME/.docker/cli-plugins\" \"$DOCKER_CONFIG/cli-plugins\"; fi\n");
            foreach (var (server, user, _) in _logins)
                script.Append("IFS= read -r p\nprintf '%s\\n' \"$p\" | docker login -u ").Append(ShellQuote.Quote(user)).Append(" --password-stdin ").Append(ShellQuote.Quote(server)).Append(" >/dev/null\n");
        }

        if (_hasFile) script.Append("cat > \"$d/in\"\n");
        script.Append(body).Append('\n');

        var stdin = new StringBuilder();
        foreach (var (_, _, password) in _logins) stdin.Append(password).Append('\n');
        if (_hasFile) stdin.Append(_fileContent);
        return new RemoteCommand("sh -c " + ShellQuote.Quote(script.ToString()), stdin.ToString(), _secrets.Distinct().ToList());
    }
}
