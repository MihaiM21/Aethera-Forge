using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh.Docker;

namespace Aethera.Api.Tests.Ssh;

public sealed class QuotingAndValidationTests
{
    [Fact]
    public void Quote_round_trips_every_hostile_string_as_exactly_one_word()
    {
        foreach (var value in PosixShell.HostileStrings().Concat(PosixShell.RandomHostileStrings(3000)))
        {
            if (value.Contains('\0')) continue;
            var quoted = ShellQuote.Quote(value);
            var words = PosixShell.Split(quoted);
            Assert.True(words.Count == 1 && words[0] == value, $"'{value}' came back as [{string.Join("|", words)}] from {quoted}");
        }
    }

    [Fact]
    public void Quote_leaves_plain_words_alone_and_quotes_leading_dashes()
    {
        Assert.Equal("nginx:1.27", ShellQuote.Quote("nginx:1.27"));
        Assert.Equal("/var/lib/aethera/projects/demo", ShellQuote.Quote("/var/lib/aethera/projects/demo"));
        Assert.Equal("'-rf'", ShellQuote.Quote("-rf")); // never an option, even if a template forgot "--"
        Assert.Equal("''", ShellQuote.Quote(""));
        Assert.Equal("'it'\\''s'", ShellQuote.Quote("it's"));
    }

    [Fact]
    public void Quote_refuses_NUL()
    {
        var ex = Assert.Throws<ServerTransportException>(() => ShellQuote.Quote("a\0b"));
        Assert.Equal(TransportErrors.CommandRejected, ex.Code);
    }

    [Fact]
    public void Quote_agrees_with_a_real_shell_when_one_is_available()
    {
        var sh = FindSh();
        if (sh is null) return; // plain Windows without Git's sh: the sshd integration tests cover the real shell
        foreach (var value in PosixShell.HostileStrings().Where(v => !v.Contains('\n') || true))
        {
            var script = "printf '%s' " + ShellQuote.Quote(value);
            var start = new System.Diagnostics.ProcessStartInfo(sh, ["-c", script]) { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(value, output);
        }
    }

    private static string? FindSh()
    {
        foreach (var candidate in new[] { "/bin/sh", @"C:\Program Files\Git\usr\bin\sh.exe", @"C:\Program Files\Git\bin\sh.exe" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    // ---- validators ------------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> BadNames() => [.. PosixShell.HostileStrings().Concat(["-x", ".hidden", "_x", "a/b", new string('a', 129), "a b", "a:b"]).Distinct()];

    [Theory, MemberData(nameof(BadNames))]
    public void Names_must_match_the_strict_pattern(string value)
    {
        Assert.False(SshValidators.IsName(value));
        Assert.Throws<ServerTransportException>(() => SshValidators.Name(value, "x"));
    }

    [Theory]
    [InlineData("web")]
    [InlineData("web-1")]
    [InlineData("Web_1.x")]
    [InlineData("0abc")]
    public void Names_that_match_pass(string value) => Assert.Equal(value, SshValidators.Name(value, "x"));

    [Theory]
    [InlineData("nginx")]
    [InlineData("nginx:1.27")]
    [InlineData("library/nginx:1.27-alpine")]
    [InlineData("ghcr.io/owner/app:v1.2.3")]
    [InlineData("localhost:5000/team/app:latest")]
    [InlineData("registry.example.com:443/a/b/c@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:0123456789ab")]
    [InlineData("0123456789abcdef")]
    public void Image_references_per_the_OCI_grammar_pass(string value) => Assert.True(SshValidators.IsImageRef(value));

    [Theory]
    [InlineData("")]
    [InlineData("-privileged")]
    [InlineData("--rm")]
    [InlineData("nginx; reboot")]
    [InlineData("nginx$(id)")]
    [InlineData("nginx`id`")]
    [InlineData("nginx && id")]
    [InlineData("nginx\nid")]
    [InlineData("NGINX")]
    [InlineData("nginx:")]
    [InlineData("nginx:tag with space")]
    [InlineData("a//b")]
    [InlineData("ngi'nx")]
    [InlineData("nginx|cat")]
    [InlineData("/nginx")]
    public void Image_references_that_are_not_valid_are_refused(string value)
    {
        Assert.False(SshValidators.IsImageRef(value));
        var ex = Assert.Throws<ServerTransportException>(() => DockerCommands.ImagePull(value, null, null));
        Assert.Equal(TransportErrors.CommandRejected, ex.Code);
    }

    [Fact]
    public void Every_hostile_string_is_refused_by_every_strict_validator()
    {
        var strict = new Action<string>[]
        {
            v => SshValidators.Name(v, "n"), v => SshValidators.ImageRef(v), v => SshValidators.ComposeProject(v), v => SshValidators.EnvName(v), v => SshValidators.LabelKey(v),
            v => SshValidators.AbsolutePath(v, "p"), v => SshValidators.RelativePath(v, "p"), v => SshValidators.Platform(v), v => SshValidators.Signal(v),
            v => SshValidators.Capability(v), v => SshValidators.Cpuset(v), v => SshValidators.LogDriver(v), v => SshValidators.PublicGitUrl(v), v => SshValidators.GitRef(v),
            v => SshValidators.Hex(v, "h"), v => SshValidators.HostIp(v), v => SshValidators.User(v), v => SshValidators.RegistryServer(v),
        };
        foreach (var value in PosixShell.HostileStrings().Concat(PosixShell.RandomHostileStrings(2000, seed: 99)))
        {
            foreach (var validate in strict)
            {
                try
                {
                    validate(value);
                    // A value that passes must be harmless: quoting it yields one word and it has no metacharacter at all.
                    Assert.All(value, c => Assert.True(c < 128 && (char.IsLetterOrDigit(c) || "_-./:=,@%+[]".Contains(c)), $"'{c}' passed in '{value}'"));
                }
                catch (ServerTransportException ex)
                {
                    Assert.Equal(TransportErrors.CommandRejected, ex.Code);
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Ports_are_integers_in_range(int port) => Assert.Throws<ServerTransportException>(() => SshValidators.Port(port, "port"));

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://gitlab.example.com/group/sub/repo")]
    public void Public_https_git_urls_are_allowed(string url) => Assert.Equal(url, SshValidators.PublicGitUrl(url));

    [Theory]
    [InlineData("http://github.com/o/r.git")]
    [InlineData("git://github.com/o/r.git")]
    [InlineData("ssh://git@github.com/o/r.git")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@github.com/o/r.git")]
    [InlineData("https://github.com/o/r.git#main")]
    [InlineData("https://github.com/o/r.git?x=1")]
    [InlineData("https://github.com/o/r.git;reboot")]
    [InlineData("https://github.com/o/$(id)")]
    [InlineData("https://github.com/o/r r")]
    [InlineData("-https://github.com/o/r")]
    [InlineData("ext::sh -c id")]
    public void Anything_else_as_a_repository_is_refused(string url) => Assert.Throws<ServerTransportException>(() => SshValidators.PublicGitUrl(url));

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.2")]
    [InlineData("v1.0.0")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public void Plain_git_refs_pass(string value) => Assert.Equal(value, SshValidators.GitRef(value));

    [Theory]
    [InlineData("--upload-pack=reboot")]
    [InlineData("a..b")]
    [InlineData("a//b")]
    [InlineData("x.lock")]
    [InlineData("a b")]
    [InlineData("main;id")]
    public void Git_refs_that_could_be_options_or_traversal_are_refused(string value) => Assert.Throws<ServerTransportException>(() => SshValidators.GitRef(value));

    [Theory]
    [InlineData("/var/lib/aethera/data", true)]
    [InlineData("/a/b-c/d_e", true)]
    [InlineData("/", true)]
    [InlineData("data", false)]
    [InlineData("/a/../b", false)]
    [InlineData("/a/./b", false)]
    [InlineData("/a,b", false)]
    [InlineData("/a b", false)]
    [InlineData("/a=b", false)]
    [InlineData("/a'b", false)]
    public void Absolute_paths_are_plain(string path, bool valid)
    {
        if (valid) Assert.Equal(path, SshValidators.AbsolutePath(path, "p"));
        else Assert.Throws<ServerTransportException>(() => SshValidators.AbsolutePath(path, "p"));
    }
}
