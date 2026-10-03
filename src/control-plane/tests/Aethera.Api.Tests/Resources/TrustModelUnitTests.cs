using System.Diagnostics;
using System.Text;
using Aethera.Api.Features.Resources.Trust;
using Aethera.Api.Features.Resources.Workloads;

namespace Aethera.Api.Tests.Resources;

/// <summary>ADR 0006: the compose inspector (YAML node tree, no typed deserialization) and its bounds.</summary>
public sealed class ComposeInspectorTests
{
    private static readonly int[] Reserved = [22, 80, 443, 2375, 2376, 5080, 9443];

    private static ComposeAnalysis Analyze(string yaml) => ComposeInspector.Analyze(yaml, Reserved);

    private static string Service(string body) => "services:\n  web:\n    image: nginx\n" + string.Join('\n', body.Split('\n').Select(l => "    " + l)) + "\n";

    [Fact]
    public void AnOrdinaryComposeFile_HasNothingRestricted()
    {
        var analysis = Analyze("""
            name: shop
            x-common: &common
              restart: unless-stopped
              environment:
                MODE: prod
            services:
              web:
                <<: *common
                image: nginx:1.27
                ports: ["8080:80", "9000", "127.0.0.1:8081:81/tcp", { target: 80, published: 8082 }]
                volumes:
                  - data:/var/lib/data
                  - /var/lib/anonymous
                  - type: volume
                    source: data
                    target: /mnt/data
                  - type: tmpfs
                    target: /tmp
                privileged: false
                cap_drop: [ALL]
                security_opt: ["no-new-privileges:true"]
                network_mode: bridge
                devices: []
                depends_on: [db]
              db:
                image: postgres:17
                network_mode: "service:web"
                volumes_from: [web]
            volumes:
              data: {}
            networks:
              backend: {}
            """);
        Assert.Empty(analysis.RestrictedOptions);
        Assert.Empty(analysis.BindSources);
    }

    public static TheoryData<string, string> Restricted() => new()
    {
        { Service("privileged: true"), "/services/web/privileged" },
        { Service("privileged: yes"), "/services/web/privileged" },
        { Service("privileged: \"true\""), "/services/web/privileged" },
        { Service("privileged: ${PRIV}"), "/services/web/privileged" },
        { Service("network_mode: host"), "/services/web/network_mode" },
        { Service("network_mode: container:other"), "/services/web/network_mode" },
        { Service("pid: host"), "/services/web/pid" },
        { Service("ipc: host"), "/services/web/ipc" },
        { Service("userns_mode: host"), "/services/web/userns_mode" },
        { Service("uts: host"), "/services/web/uts" },
        { Service("cap_add: [SYS_ADMIN]"), "/services/web/cap_add" },
        { Service("cap_add:\n  - NET_ADMIN"), "/services/web/cap_add" },
        { Service("devices: [\"/dev/sda:/dev/xvda\"]"), "/services/web/devices" },
        { Service("device_cgroup_rules: [\"c 1:3 mr\"]"), "/services/web/device_cgroup_rules" },
        { Service("security_opt: [\"seccomp=unconfined\"]"), "/services/web/security_opt/0" },
        { Service("security_opt: [\"no-new-privileges:true\", \"apparmor:unconfined\"]"), "/services/web/security_opt/1" },
        { Service("security_opt: [\"label=disable\"]"), "/services/web/security_opt/0" },
        { Service("cgroup_parent: system.slice"), "/services/web/cgroup_parent" },
        { Service("env_file: [/etc/environment]"), "/services/web/env_file" },
        { Service("volumes_from: [\"container:aethera-api\"]"), "/services/web/volumes_from/0" },
        { Service("extends:\n  file: ../other.yml\n  service: x"), "/services/web/extends/file" },
        { Service("build: ../.."), "/services/web/build" },
        { Service("build:\n  context: /srv/app"), "/services/web/build/context" },
        { Service("build:\n  context: .\n  dockerfile: ../../Dockerfile"), "/services/web/build/dockerfile" },
        // host bind mounts, every spelling
        { Service("volumes: [\"/etc:/host-etc:ro\"]"), "/services/web/volumes/0" },
        { Service("volumes: [\"/var/run/docker.sock:/var/run/docker.sock\"]"), "/services/web/volumes/0" },
        { Service("volumes: [\"./data:/data\"]"), "/services/web/volumes/0" },
        { Service("volumes: [\"../data:/data\"]"), "/services/web/volumes/0" },
        { Service("volumes: [\"~/data:/data\"]"), "/services/web/volumes/0" },
        { Service("volumes: [\"${DATA_DIR}:/data\"]"), "/services/web/volumes/0" },
        { Service("volumes:\n  - data:/ok\n  - /srv:/srv"), "/services/web/volumes/1" },
        { Service("volumes:\n  - type: bind\n    source: /srv\n    target: /srv"), "/services/web/volumes/0/source" },
        { Service("volumes:\n  - type: volume\n    source: /srv\n    target: /srv"), "/services/web/volumes/0/source" },
        { Service("volumes:\n  - source: /srv\n    target: /srv"), "/services/web/volumes/0/source" },
        // published ports
        { Service("ports: [\"80:80\"]"), "/services/web/ports/0" },
        { Service("ports: [\"127.0.0.1:443:8443\"]"), "/services/web/ports/0" },
        { Service("ports: [\"1023:1023/udp\"]"), "/services/web/ports/0" },
        { Service("ports: [\"5080:80\"]"), "/services/web/ports/0" },
        { Service("ports: [\"5000-5100:80\"]"), "/services/web/ports/0" },
        { Service("ports: [\"${PORT}:80\"]"), "/services/web/ports/0" },
        { Service("ports:\n  - target: 80\n    published: 22"), "/services/web/ports/0" },
        // merge keys and aliases cannot hide an option
        { "x-base: &base\n  privileged: true\nservices:\n  web:\n    <<: *base\n    image: nginx\n", "/services/web/privileged" },
        { "x-a: &a\n  cap_add: [ALL]\nx-b: &b\n  pid: host\nservices:\n  web:\n    <<: [*a, *b]\n    image: nginx\n", "/services/web/pid" },
        // top level
        { "include:\n  - /etc/other.yml\nservices:\n  web:\n    image: nginx\n", "/include" },
        { "services:\n  web:\n    image: nginx\nvolumes:\n  data:\n    driver_opts:\n      type: none\n      o: bind\n      device: /etc\n", "/volumes/data/driver_opts" },
        { "services:\n  web:\n    image: nginx\nsecrets:\n  s:\n    file: /etc/shadow\n", "/secrets/s/file" },
        { "services:\n  web:\n    image: nginx\nconfigs:\n  c:\n    file: ./conf\n", "/configs/c/file" },
    };

    [Theory]
    [MemberData(nameof(Restricted))]
    public void RestrictedOptions_AreFound_WithTheirPointer(string yaml, string pointer) =>
        Assert.Contains(pointer, Analyze(yaml).RestrictedOptions.Select(f => f.Pointer));

    [Fact]
    public void Findings_ListEveryOffendingKey_AndEscapePointers()
    {
        var analysis = Analyze("""
            services:
              "a/b~c":
                image: x
                privileged: true
                pid: host
              db:
                image: y
                volumes: ["/srv:/srv"]
            """);
        Assert.Equal(["/services/a~1b~0c/privileged", "/services/a~1b~0c/pid", "/services/db/volumes/0"], analysis.RestrictedOptions.Select(f => f.Pointer));
        Assert.All(analysis.RestrictedOptions, f => Assert.NotEmpty(f.Reason));
    }

    [Fact]
    public void BindSources_AreReportedForTheHostPathPolicy()
    {
        var analysis = Analyze("""
            services:
              web:
                image: x
                volumes: ["/srv/data:/data", "named:/n", { type: bind, source: ./rel, target: /r }]
            volumes:
              odd:
                driver_opts: { type: none, o: bind, device: /var/run }
            secrets:
              s: { file: /etc/passwd }
            """);
        Assert.Equal(
            [("/services/web/volumes/0", "/srv/data"), ("/services/web/volumes/2/source", "./rel"), ("/volumes/odd/driver_opts/device", "/var/run"), ("/secrets/s/file", "/etc/passwd")],
            analysis.BindSources.Select(b => (b.Pointer, b.Source)));
    }

    [Theory]
    [InlineData("services: [unclosed")]
    [InlineData("services:\n\tweb: {}")]
    [InlineData("- just\n- a list")]
    [InlineData("just a string")]
    [InlineData("")]
    [InlineData("version: '3'\n")]
    [InlineData("services: not-a-mapping\n")]
    [InlineData("services:\n  web: not-a-mapping\n")]
    [InlineData("services:\n  web:\n    image: a\n---\nservices: {}\n")]
    [InlineData("services:\n  web:\n    image: a\n    image: b\n")] // duplicate key
    [InlineData("services:\n  web: *undefined\n")]
    public void InvalidDocuments_AreRefused(string yaml) => Assert.Throws<ComposeInvalidException>(() => Analyze(yaml));

    [Fact]
    public void TooManyAliases_AreRefused_BeforeAnythingIsExpanded()
    {
        // The classic "billion laughs": each level lists the previous one nine times.
        var bomb = new StringBuilder("x-l0: &l0 [lol, lol, lol, lol, lol, lol, lol, lol, lol]\n");
        for (var i = 1; i <= 9; i++) bomb.Append($"x-l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 9))}]\n");
        bomb.Append("services:\n  web:\n    image: nginx\n");

        var error = Assert.Throws<ComposeInvalidException>(() => Analyze(bomb.ToString()));
        Assert.Contains("aliases", error.Message);
    }

    [Fact]
    public void FiftyAliases_AreStillFine_FiftyOneAreNot()
    {
        string Doc(int aliases) => "x: &a [1]\nl: [" + string.Join(", ", Enumerable.Repeat("*a", aliases)) + "]\nservices:\n  web:\n    image: nginx\n";
        Assert.Empty(Analyze(Doc(ComposeInspector.MaxAliases)).RestrictedOptions);
        Assert.Throws<ComposeInvalidException>(() => Analyze(Doc(ComposeInspector.MaxAliases + 1)));
    }

    [Fact]
    public void ExponentialMergeChains_HitTheVisitBudget_NotTheCpu()
    {
        // Few aliases (49) but 2^24 merge paths: the traversal budget stops it.
        var doc = new StringBuilder("x-m0: &m0 { k: v }\n");
        for (var i = 1; i <= 24; i++) doc.Append($"x-m{i}: &m{i} {{ <<: [*m{i - 1}, *m{i - 1}] }}\n");
        doc.Append("services:\n  web:\n    <<: *m24\n    image: nginx\n");

        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<ComposeInvalidException>(() => Analyze(doc.ToString()));
        Assert.Contains("too complex", error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public void DeepNesting_AndHugeDocuments_AreRefused()
    {
        var deep = "services:\n  web:\n    image: x\n    labels: " + new string('[', ComposeInspector.MaxDepth + 5) + new string(']', ComposeInspector.MaxDepth + 5) + "\n";
        Assert.Contains("deeper", Assert.Throws<ComposeInvalidException>(() => Analyze(deep)).Message);

        // Below the character cap of the request validator, above the byte cap of the inspector (two bytes per character).
        var wide = "services:\n  web:\n    image: x\n    command: \"" + new string('é', 140_000) + "\"\n";
        Assert.True(wide.Length < ComposeInspector.MaxChars);
        Assert.Contains("larger", Assert.Throws<ComposeInvalidException>(() => Analyze(wide)).Message);

        var many = "services:\n  web:\n    image: x\n    environment: [" + string.Join(", ", Enumerable.Range(0, ComposeInspector.MaxEvents + 1)) + "]\n";
        Assert.Throws<ComposeInvalidException>(() => Analyze(many));
    }

    [Fact]
    public void YamlTags_AreNotInstantiated()
    {
        // A typed deserializer could construct objects from tags; the node tree just carries text.
        var analysis = Analyze("services:\n  web:\n    image: !!python/object/apply:os.system [id]\n    privileged: !!bool true\n");
        Assert.Contains("/services/web/privileged", analysis.RestrictedOptions.Select(f => f.Pointer));
    }
}

/// <summary>ADR 0006: host path normalization, the denylist and its allowlist.</summary>
public sealed class HostPathPolicyTests
{
    private static TrustPolicy Policy(params string[] allowlist) => new(new TrustOptions { HostPathAllowlist = allowlist.Length == 0 ? null : allowlist });

    [Theory]
    [InlineData("/srv/data", "/srv/data")]
    [InlineData("//srv///data//", "/srv/data")]
    [InlineData("/srv/./data/.", "/srv/data")]
    [InlineData("/", "/")]
    [InlineData("/./", "/")]
    [InlineData("/var/./run//docker.sock", "/var/run/docker.sock")]
    public void Normalizes(string input, string expected)
    {
        Assert.True(TrustPolicy.TryNormalizeHostPath(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/srv/../etc")]
    [InlineData("/..")]
    [InlineData("/srv/data\n")]
    [InlineData("/srv/da ta")]
    [InlineData("/srv/a:b")]
    [InlineData("/srv/a,b")]
    [InlineData("/srv/a\\b")]
    [InlineData("/srv/\"x\"")]
    public void Rejects(string input) => Assert.False(TrustPolicy.TryNormalizeHostPath(input, out _));

    [Theory]
    [InlineData("/")]
    [InlineData("/var/run")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/run")]
    [InlineData("/run/docker.sock")]
    [InlineData("/run/user/1000")]
    [InlineData("/proc")]
    [InlineData("/proc/1/root")]
    [InlineData("/sys")]
    [InlineData("/sys/fs/cgroup")]
    [InlineData("/dev")]
    [InlineData("/dev/sda1")]
    [InlineData("/etc")]
    [InlineData("/etc/shadow")]
    [InlineData("/boot")]
    [InlineData("/root")]
    [InlineData("/root/.ssh")]
    [InlineData("/var/lib/aethera")]
    [InlineData("/var/lib/aethera/secrets")]
    [InlineData("/var/lib/aethera/pki")]
    [InlineData("/var/lib/docker")]
    [InlineData("/var/lib/docker/volumes")]
    [InlineData("/var/lib/containerd")]
    public void Denied(string path)
    {
        Assert.True(TrustPolicy.TryNormalizeHostPath(path, out var normalized));
        Assert.NotNull(Policy().HostPathDenial(normalized));
    }

    [Theory]
    [InlineData("/srv/data")]
    [InlineData("/mnt/disk1")]
    [InlineData("/home/deploy/files")]
    [InlineData("/var/lib/aethera/volumes")]
    [InlineData("/var/lib/aethera/volumes/app")]
    [InlineData("/var/lib/aethera/volumes/app/deep/er")]
    [InlineData("/var/lib/aethera-other")] // a sibling with the same prefix is not "below"
    [InlineData("/var/running")]
    [InlineData("/etcetera")]
    [InlineData("/rooted")]
    [InlineData("/devices")]
    [InlineData("/var/lib/docker-data")]
    public void Allowed(string path)
    {
        Assert.True(TrustPolicy.TryNormalizeHostPath(path, out var normalized));
        Assert.Null(Policy().HostPathDenial(normalized));
    }

    [Fact]
    public void TheAllowlistIsConfigurable_AndReplacesTheDefault()
    {
        var policy = Policy("/etc/aethera-mounts/", "/srv/shared");
        Assert.Null(policy.HostPathDenial("/etc/aethera-mounts/app"));
        Assert.Null(policy.HostPathDenial("/srv/shared/x"));
        Assert.NotNull(policy.HostPathDenial("/etc/passwd"));
        Assert.NotNull(policy.HostPathDenial("/etc/aethera-mounts-evil/x")); // a prefix means a directory, not a string prefix
        Assert.NotNull(policy.HostPathDenial("/var/lib/aethera/volumes/app")); // the default is gone once an allowlist is configured
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/var")]
    [InlineData("/var/lib")]
    [InlineData("/etc")]
    [InlineData("/var/lib/aethera")]
    [InlineData("/var/run")]
    [InlineData("relative")]
    [InlineData("/a/../b")]
    public void AnAllowlistThatWouldReopenADeniedDirectory_StopsTheStartup(string entry) =>
        Assert.Throws<InvalidOperationException>(() => Policy(entry));

    [Fact]
    public void ReservedPorts_DefaultAndConfigurable()
    {
        var defaults = Policy();
        Assert.All(new[] { 22, 80, 443, 2375, 2376, 5080, 9443 }, p => Assert.True(defaults.IsReservedPort(p), p.ToString()));
        Assert.False(defaults.IsReservedPort(8080));

        var custom = new TrustPolicy(new TrustOptions { ReservedPorts = [8123] });
        Assert.True(custom.IsReservedPort(8123));
        Assert.False(custom.IsReservedPort(443));
        Assert.True(custom.IsPrivilegedPort(443)); // the privileged range is not configurable
        Assert.False(custom.IsPrivilegedPort(1024));
    }
}

/// <summary>ADR 0006: build input rules (paths that stay in the repository, refs and URLs that cannot inject options).</summary>
public sealed class BuildInputRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("apps/web")]
    [InlineData("./apps/web/")]
    [InlineData("Dockerfile.prod")]
    [InlineData("with space/dir")]
    [InlineData("a/.hidden/b")]
    [InlineData("a/..b/c")]
    [InlineData("dist")]
    public void RelativePaths_Accepted(string? path) => Assert.True(BuildInputRules.IsSafeRelativePath(path));

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("a/b/..")]
    [InlineData("/")]
    [InlineData("/etc/passwd")]
    [InlineData("~")]
    [InlineData("~/x")]
    [InlineData("C:\\Windows")]
    [InlineData("C:/Windows")]
    [InlineData("c:")]
    [InlineData("a\\b")]
    [InlineData("..\\x")]
    [InlineData("-rf")]
    [InlineData("--build-arg=x")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("x\n")]
    public void RelativePaths_Rejected(string path) => Assert.False(BuildInputRules.IsSafeRelativePath(path));

    [Theory]
    [InlineData("main")]
    [InlineData("feature/login-form")]
    [InlineData("release-1.2.3")]
    [InlineData("v2")]
    [InlineData("user/fix_123")]
    public void GitRefs_Accepted(string reference) => Assert.True(BuildInputRules.IsSafeGitRef(reference));

    [Theory]
    [InlineData("")]
    [InlineData("-x")]
    [InlineData("--upload-pack=touch /tmp/x")]
    [InlineData("-")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("main\n")]
    [InlineData("a..b")]
    [InlineData("a~1")]
    [InlineData("a^")]
    [InlineData("a:b")]
    [InlineData("a?")]
    [InlineData("a*")]
    [InlineData("a[0]")]
    [InlineData("a\\b")]
    [InlineData("a@{u}")]
    public void GitRefs_Rejected(string reference) => Assert.False(BuildInputRules.IsSafeGitRef(reference));

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("http://git.internal:8080/org/repo")]
    [InlineData("https://user:token@gitlab.com/org/repo.git")]
    [InlineData("ssh://git@github.com/org/repo.git")]
    [InlineData("ssh://git@github.com:2222/org/repo.git")]
    [InlineData("git://host.example.com/repo.git")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("deploy.user@git.example.com:team/app")]
    public void RepositoryUrls_Accepted(string url) => Assert.True(BuildInputRules.IsSafeRepositoryUrl(url));

    [Theory]
    [InlineData("")]
    [InlineData("-oProxyCommand=touch/tmp/pwn")]
    [InlineData("--upload-pack=x")]
    [InlineData("ssh://-oProxyCommand=touch%20/tmp/pwn/repo")]
    [InlineData("ssh://-oProxyCommand=touch%20/tmp/pwn@host/repo")]
    [InlineData("ssh://user@-oProxyCommand=x/repo")]
    [InlineData("ssh://-host/repo")]
    [InlineData("https://-evil.example.com/repo")]
    [InlineData("git://-x/repo")]
    [InlineData("git@-oProxyCommand=x:org/repo.git")]
    [InlineData("-oProxyCommand=x@host:org/repo.git")]
    [InlineData("file:///etc/passwd")]
    [InlineData("FILE:///etc/passwd")]
    [InlineData("file:/etc/passwd")]
    [InlineData("ext::sh -c id")]
    [InlineData("ext::sh%20-c%20id")]
    [InlineData("EXT::sh")]
    [InlineData("fd::17")]
    [InlineData("https://host/repo ")]
    [InlineData("https://host/re po")]
    [InlineData("https://host/repo\n")]
    [InlineData("https://host/repo\0")]
    [InlineData("https://host/repo\u0001")]
    [InlineData("git@host:org/repo\n")]
    [InlineData("ftp://host/repo")]
    [InlineData("https://")]
    [InlineData("ssh:///repo")]
    [InlineData("/local/path")]
    [InlineData("../repo")]
    public void RepositoryUrls_Rejected(string url) => Assert.False(BuildInputRules.IsSafeRepositoryUrl(url));
}
