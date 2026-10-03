using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.DomainNames;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Features.Resources.Servers;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

/// <summary>
/// .NET <c>$</c> also matches before a trailing <c>\n</c>, so <c>^[a-z]+$</c> accepts <c>"abc\n"</c>. Every validator pattern uses <c>\A...\z</c>;
/// each is pinned here with a regression test, and a reflection guard fails when a new one slips in with <c>^</c> / <c>$</c>.
/// </summary>
public sealed class RegexAnchorRegressionTests
{
    [Theory]
    [InlineData("FOO_BAR")]
    [InlineData("_x1")]
    public void EnvVarKey(string valid)
    {
        Assert.True(EnvironmentVariable.IsValidKey(valid));
        Assert.False(EnvironmentVariable.IsValidKey(valid + "\n"));
    }

    [Fact]
    public void Slug()
    {
        Assert.True(Aethera.Domain.Slug.IsValid("my-app"));
        Assert.False(Aethera.Domain.Slug.IsValid("my-app\n"));
    }

    [Fact]
    public void Image()
    {
        Assert.True(ApplicationRules.IsImage("ghcr.io/org/app"));
        Assert.False(ApplicationRules.IsImage("ghcr.io/org/app\n"));
    }

    [Fact]
    public void Tag()
    {
        Assert.True(ApplicationRules.IsTag("1.27-alpine"));
        Assert.False(ApplicationRules.IsTag("1.27-alpine\n"));
    }

    [Fact]
    public void Commit()
    {
        Assert.True(ApplicationRules.IsCommit("3f9c2d1"));
        Assert.False(ApplicationRules.IsCommit("3f9c2d1\n"));
    }

    [Fact]
    public void Platform()
    {
        Assert.True(ApplicationRules.IsPlatform("linux/amd64"));
        Assert.False(ApplicationRules.IsPlatform("linux/amd64\n"));
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git")]
    public void RepositoryUrl(string valid)
    {
        Assert.True(ApplicationRules.IsRepositoryUrl(valid));
        Assert.False(ApplicationRules.IsRepositoryUrl(valid + "\n"));
    }

    [Theory]
    [InlineData("ghcr.io")]
    [InlineData("registry.example.com:5000")]
    [InlineData("https://index.docker.io/v1/")]
    public void RegistryUrl(string valid)
    {
        Assert.True(RegistryRules.IsValidUrl(valid));
        Assert.False(RegistryRules.IsValidUrl(valid + "\n"));
        Assert.False(RegistryRules.IsValidUrl(valid + "/x\ny"));
    }

    [Theory]
    [InlineData("git")]
    [InlineData("deploy_1")]
    [InlineData("svc$")]
    public void SshUser(string valid)
    {
        Assert.True(ServerRules.IsValidSshUser(valid));
        Assert.False(ServerRules.IsValidSshUser(valid + "\n"));
    }

    [Fact]
    public void VolumeName()
    {
        Assert.True(VolumeRules.IsValidName("data-1"));
        Assert.False(VolumeRules.IsValidName("data-1\n"));
    }

    [Theory]
    [InlineData("exa\nmple.com")]
    [InlineData("example.com\nevil")]
    [InlineData("a\n.example.com")]
    [InlineData("example\n.com")]
    public void HostnameLabels(string value) => Assert.False(HostnameRules.TryNormalize(value, allowWildcard: true, out _, out _));

    [Fact]
    public void HostnameLabel_TrailingNewlineOfTheWholeValue_IsTrimmedNotKept()
    {
        Assert.True(HostnameRules.TryNormalize("example.com\n", allowWildcard: false, out var normalized, out _));
        Assert.Equal("example.com", normalized);
    }

    [Fact]
    public void DomainPathPrefix()
    {
        Assert.True(DomainRules.TryNormalizePath("/api", out _));
        Assert.False(DomainRules.TryNormalizePath("/api\n", out _));
        Assert.False(DomainRules.TryNormalizePath("\n/api", out _));
    }

    [Fact]
    public void EveryRegexOfTheResourceAndDomainCode_IsAnchoredWithAAndZ()
    {
        var assemblies = new[] { typeof(DomainRules).Assembly, typeof(Aethera.Domain.Slug).Assembly };
        var checkedPatterns = 0;
        foreach (var method in assemblies.SelectMany(a => a.GetTypes()).SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                     .Where(m => m.GetCustomAttribute<GeneratedRegexAttribute>() is not null))
        {
            var ns = method.DeclaringType!.Namespace ?? "";
            // Request-id echo (Aethera.Api.Http), the audit redaction (Infrastructure) and the like are outside the validators.
            if (!(ns.StartsWith("Aethera.Api.Features.Resources", StringComparison.Ordinal)
                  || ns.StartsWith("Aethera.Api.Features.Auth", StringComparison.Ordinal) || ns.StartsWith("Aethera.Domain", StringComparison.Ordinal))) continue;

            var pattern = method.GetCustomAttribute<GeneratedRegexAttribute>()!.Pattern;
            checkedPatterns++;
            Assert.True(pattern.StartsWith(@"\A", StringComparison.Ordinal), $"{method.DeclaringType.Name}.{method.Name} must start with \\A: {pattern}");
            Assert.True(pattern.EndsWith(@"\z", StringComparison.Ordinal), $"{method.DeclaringType.Name}.{method.Name} must end with \\z: {pattern}");
            Assert.DoesNotMatch(@"(?<!\\)\$", pattern); // an unescaped $ anywhere
            Assert.DoesNotMatch(@"(?<![\[\\])\^", pattern); // a ^ that is not a character-class negation or escaped
        }

        Assert.True(checkedPatterns >= 10, $"only {checkedPatterns} patterns were inspected");
    }
}

/// <summary>ADR 0003/0006: the dotenv export is single-quoted so that sourcing the file in a shell cannot execute or expand anything.</summary>
public sealed class DotEnvExportTests
{
    [Fact]
    public void Export_QuotesEveryValue_AndEscapesSingleQuotes()
    {
        var text = DotEnv.Format([
            KeyValuePair.Create("A", "plain"), KeyValuePair.Create("B", ""), KeyValuePair.Create("C", "it's"), KeyValuePair.Create("D", "$(id) `id` ${HOME}"),
            KeyValuePair.Create("E", "a\nb"), KeyValuePair.Create("F", "\"x\" \\n"),
        ]);
        Assert.Equal("A='plain'\nB=''\nC='it'\\''s'\nD='$(id) `id` ${HOME}'\nE='a\nb'\nF='\"x\" \\n'\n", text);
    }

    [Theory]
    [InlineData("A='x'", "x")]
    [InlineData("A=x", "x")]
    [InlineData("A=\"x\"", "x")]
    [InlineData("A='it'\\''s'", "it's")]
    [InlineData("A=''\\'''", "'")]
    [InlineData("A='a'\\''b'\\''c'", "a'b'c")]
    [InlineData("A='$HOME \\n'", "$HOME \\n")]
    public void Import_ReadsQuotedAndUnquotedValues(string line, string expected)
    {
        var (values, errors) = DotEnv.Parse(line + "\n");
        Assert.Empty(errors);
        Assert.Equal(expected, Assert.Single(values).Value);
    }

    [Fact]
    public void Import_StillRejectsGarbageAfterTheClosingQuote()
    {
        Assert.NotEmpty(DotEnv.Parse("A='x' junk\n").Errors);
        Assert.NotEmpty(DotEnv.Parse("A='x'\\\n").Errors);
        Assert.NotEmpty(DotEnv.Parse("A='unclosed\n").Errors);
    }

    [Fact]
    public void Export_RoundTripsThroughImport_ForHostileValues()
    {
        var values = new[]
        {
            "", "'", "''", "'''", "a'b", "'a'", "\\", "\\'", "'\\", "$", "$(touch /tmp/pwned)", "`touch /tmp/pwned`", "; rm -rf /", "#", " # x", "  ", "x = y",
            "=", "export A=1", "\"", "\"'\"'", "multi\nline", "multi\n'line'\n", "tab\there", "ünï©ode ✓", new string('x', 5000), "A='b'\nC='d'", "'\n'",
        };
        var original = values.Select((v, i) => KeyValuePair.Create($"K{i}", v)).ToList();
        var (parsed, errors) = DotEnv.Parse(DotEnv.Format(original));
        Assert.Empty(errors);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void AShellThatSourcesTheExport_SeesPlainTextAndRunsNothing()
    {
        const string bash = "/bin/bash";
        if (!File.Exists(bash)) return;

        var marker = Path.Combine(Path.GetTempPath(), "aethera-dotenv-" + Guid.NewGuid().ToString("N"));
        var values = new Dictionary<string, string>
        {
            ["PLAIN"] = "value", ["QUOTE"] = "it's", ["SUBST"] = $"$(touch {marker}-a)", ["TICK"] = $"`touch {marker}-b`", ["VAR"] = "${HOME} $HOME",
            ["SEMI"] = $"x; touch {marker}-c", ["NEWLINE"] = "a\nb", ["BANG"] = "!history", ["BACKSLASH"] = "a\\nb\\", ["EMPTY"] = "",
        };
        var file = Path.Combine(Path.GetTempPath(), "aethera-dotenv-" + Guid.NewGuid().ToString("N") + ".env");
        File.WriteAllText(file, DotEnv.Format(values));
        try
        {
            var info = new ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("set -e; source \"$1\"; for k in PLAIN QUOTE SUBST TICK VAR SEMI NEWLINE BANG BACKSLASH EMPTY; do printf '%s\\0%s\\0' \"$k\" \"${!k}\"; done");
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(file);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);

            var parts = output.Split('\0');
            var seen = Enumerable.Range(0, values.Count).ToDictionary(i => parts[2 * i], i => parts[2 * i + 1]);
            Assert.Equal(values, seen);
            Assert.False(File.Exists(marker + "-a") || File.Exists(marker + "-b") || File.Exists(marker + "-c"), "a command in the export was executed");
        }
        finally
        {
            File.Delete(file);
            foreach (var suffix in new[] { "-a", "-b", "-c" }) File.Delete(marker + suffix);
        }
    }
}
