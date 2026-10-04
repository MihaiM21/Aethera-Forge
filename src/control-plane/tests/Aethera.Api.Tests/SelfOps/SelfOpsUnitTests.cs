using System.Net;
using System.Text;
using Aethera.Domain;
using Aethera.Infrastructure.SelfOps;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Tests.SelfOps;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")]
    [InlineData("1.2.10", "1.2.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("v1.1.0", "1.0.5")]
    public void Orders_by_semver_precedence(string higher, string lower)
    {
        Assert.True(SemanticVersion.Parse(higher) > SemanticVersion.Parse(lower), $"{higher} > {lower}");
        Assert.True(SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher));
    }

    [Fact]
    public void Build_metadata_and_the_v_prefix_do_not_change_the_version()
    {
        Assert.Equal(SemanticVersion.Parse("1.2.3"), SemanticVersion.Parse("v1.2.3+abc123"));
        Assert.Equal("1.2.3-rc.1", SemanticVersion.Parse("v1.2.3-rc.1+build.5").ToString());
        Assert.True(SemanticVersion.Parse("1.2.3-rc.1").IsPreRelease);
    }

    [Fact]
    public void Equality_operators_compare_versions_not_references()
    {
        var a = SemanticVersion.Parse("1.2.3");
        var b = SemanticVersion.Parse("v1.2.3+other");
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a != SemanticVersion.Parse("1.2.4"));
        Assert.True(a >= b && a <= b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("latest")]
    [InlineData("1.2.3-")]
    public void Rejects_what_is_not_a_semantic_version(string text) => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void The_running_version_is_a_valid_semver()
    {
        Assert.True(SemanticVersion.TryParse(AppVersion.Current, out _), AppVersion.Current);
    }
}

public sealed class ReleaseCheckerTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string?> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ReleaseChecker Checker(Stub stub, string current = "1.0.0", Action<UpdateOptions>? configure = null, FakeClock? clock = null)
    {
        var options = new UpdateOptions { ApiBaseUrl = "https://api.test", Repository = "acme/aethera" };
        configure?.Invoke(options);
        return new ReleaseChecker(new HttpClient(stub), Options.Create(options), clock ?? new FakeClock(), NullLogger<ReleaseChecker>.Instance) { CurrentVersion = current };
    }

    private const string Release = """
        {"tag_name":"v1.2.0","html_url":"https://github.com/acme/aethera/releases/tag/v1.2.0","body":"Fixes things.","prerelease":false,"draft":false,
         "published_at":"2026-10-01T10:00:00Z",
         "assets":[{"name":"aethera-agent-linux-amd64","browser_download_url":"https://dl.test/amd64","size":10},
                   {"name":"SHA256SUMS","browser_download_url":"https://dl.test/sums","size":5}]}
        """;

    [Fact]
    public async Task Reads_the_latest_release_with_notes_and_assets_and_says_an_update_is_available()
    {
        var stub = new Stub(_ => Json(Release));
        var checker = Checker(stub);

        var latest = await checker.CheckAsync(default);

        Assert.NotNull(latest);
        Assert.Equal("1.2.0", latest.Version);
        Assert.Equal("v1.2.0", latest.Tag);
        Assert.Equal("Fixes things.", latest.Notes);
        Assert.Equal(2, latest.Assets.Count);
        Assert.True(checker.UpdateAvailable);
        Assert.Null(checker.LastError);
        Assert.NotNull(checker.CheckedAt);
        Assert.Equal("https://api.test/repos/acme/aethera/releases/latest", stub.Requests.Single().ToString());
        Assert.StartsWith("Aethera/1.0.0", stub.UserAgents.Single());
    }

    [Theory]
    [InlineData("1.2.0", false)]
    [InlineData("1.3.0", false)]
    [InlineData("1.2.0-rc.1", true)]
    [InlineData("0.0.0-dev", true)]
    public async Task An_update_is_only_offered_for_a_strictly_newer_release(string current, bool available)
    {
        var checker = Checker(new Stub(_ => Json(Release)), current);
        await checker.CheckAsync(default);
        Assert.Equal(available, checker.UpdateAvailable);
    }

    [Fact]
    public async Task No_published_release_is_not_an_error()
    {
        var checker = Checker(new Stub(_ => Json("{}", HttpStatusCode.NotFound)));
        Assert.Null(await checker.CheckAsync(default));
        Assert.Null(checker.LastError);
        Assert.False(checker.UpdateAvailable);
    }

    [Fact]
    public async Task A_failing_check_keeps_the_last_good_answer_and_reports_why()
    {
        var fail = false;
        var clock = new FakeClock();
        var checker = Checker(new Stub(_ => fail ? Json("rate limited", HttpStatusCode.Forbidden) : Json(Release)), clock: clock);
        await checker.CheckAsync(default);
        fail = true;
        clock.UtcNow = clock.UtcNow.AddHours(6);
        var again = await checker.CheckAsync(default);

        Assert.Equal("1.2.0", again!.Version);
        Assert.True(checker.UpdateAvailable);
        Assert.Contains("403", checker.LastError);
        Assert.Equal(clock.UtcNow, checker.CheckedAt);
    }

    [Fact]
    public async Task A_network_failure_is_reported_not_thrown()
    {
        var checker = Checker(new Stub(_ => throw new HttpRequestException("name resolution failed")));
        Assert.Null(await checker.CheckAsync(default));
        Assert.Contains("name resolution failed", checker.LastError);
    }

    [Fact]
    public async Task Garbage_in_the_response_is_reported_not_thrown()
    {
        var checker = Checker(new Stub(_ => Json("<html>proxy login</html>")));
        Assert.Null(await checker.CheckAsync(default));
        Assert.NotNull(checker.LastError);
    }

    [Fact]
    public async Task Pre_releases_are_ignored_unless_asked_for_and_drafts_never_count()
    {
        const string list = """
            [{"tag_name":"v2.0.0-rc.1","prerelease":true,"draft":false,"assets":[]},
             {"tag_name":"v3.0.0","prerelease":false,"draft":true,"assets":[]},
             {"tag_name":"v1.5.0","prerelease":false,"draft":false,"assets":[]},
             {"tag_name":"nightly","prerelease":true,"draft":false,"assets":[]}]
            """;
        var stub = new Stub(_ => Json(list));
        var checker = Checker(stub, configure: o => o.IncludePrereleases = true);

        var latest = await checker.CheckAsync(default);

        Assert.Equal("2.0.0-rc.1", latest!.Version);
        Assert.True(latest.PreRelease);
        Assert.Contains("/releases?per_page=", stub.Requests.Single().ToString());
    }

    [Fact]
    public async Task Disabled_checks_never_touch_the_network()
    {
        var stub = new Stub(_ => throw new InvalidOperationException("must not be called"));
        var checker = Checker(stub, configure: o => o.Enabled = false);
        Assert.Null(await checker.CheckAsync(default));
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("not-a-repo")]
    [InlineData("a/b/c")]
    [InlineData("a b/c")]
    public async Task A_malformed_repository_setting_is_reported(string repository)
    {
        var stub = new Stub(_ => Json(Release));
        var checker = Checker(stub, configure: o => o.Repository = repository);
        await checker.CheckAsync(default);
        Assert.Contains("owner/name", checker.LastError);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Finds_the_checksum_of_an_asset_in_SHA256SUMS()
    {
        var sha = new string('a', 64);
        var other = new string('b', 64);
        var stub = new Stub(r => r.RequestUri!.AbsoluteUri == "https://dl.test/sums"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{other}  install.sh\n{sha.ToUpperInvariant()} *aethera-agent-linux-amd64\n") }
            : Json(Release));
        var checker = Checker(stub);
        var release = (await checker.CheckAsync(default))!;

        Assert.Equal(sha, await checker.GetChecksumAsync(release, "aethera-agent-linux-amd64", default));
        Assert.Null(await checker.GetChecksumAsync(release, "aethera-agent-linux-arm64", default));
    }
}

public sealed class UpdaterControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aethera-updater-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private UpdaterControl Control() => new(Options.Create(new UpdateOptions { ControlDirectory = _dir }), _clock);

    private void Alive(DateTimeOffset at)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "alive");
        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Without_an_updater_nothing_is_available_and_the_state_is_idle()
    {
        var control = Control();
        Assert.False(control.IsAvailable());
        var status = await control.GetStatusAsync(default);
        Assert.Equal(new UpdaterStatus(false, "idle", null, null, null), status);
    }

    [Fact]
    public void A_fresh_heartbeat_makes_the_updater_available_and_a_stale_one_does_not()
    {
        var control = Control();
        Alive(_clock.UtcNow.AddSeconds(-10));
        Assert.True(control.IsAvailable());
        Alive(_clock.UtcNow.AddMinutes(-5));
        Assert.False(control.IsAvailable());
    }

    [Fact]
    public async Task A_request_is_written_atomically_and_reads_back_as_requested()
    {
        var control = Control();
        Alive(_clock.UtcNow);
        await control.RequestAsync("1.2.0", Guid.NewGuid(), default);

        var status = await control.GetStatusAsync(default);

        Assert.Equal("requested", status.State);
        Assert.Equal("1.2.0", status.TargetVersion);
        Assert.True(UpdaterControl.InFlight(status));
        Assert.False(File.Exists(Path.Combine(_dir, "request.json.tmp")));
    }

    [Theory]
    [InlineData("pulling", true)]
    [InlineData("backing_up", true)]
    [InlineData("restarting", true)]
    [InlineData("done", false)]
    [InlineData("failed", false)]
    [InlineData("idle", false)]
    public async Task Reports_what_the_updater_wrote_and_only_unfinished_states_are_in_flight(string state, bool inFlight)
    {
        var control = Control();
        Alive(_clock.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(_dir, "status.json"), $$"""{"state":"{{state}}","version":"1.2.0","message":"hello","updatedAt":"2026-10-04T12:00:00Z"}""");

        var status = await control.GetStatusAsync(default);

        Assert.Equal(state, status.State);
        Assert.Equal("hello", status.Message);
        Assert.Equal(inFlight, UpdaterControl.InFlight(status));
    }

    [Fact]
    public async Task A_damaged_status_file_reads_as_idle()
    {
        var control = Control();
        Alive(_clock.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(_dir, "status.json"), "{not json");
        Assert.Equal("idle", (await control.GetStatusAsync(default)).State);
    }
}

public sealed class BackupFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aethera-backups-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private DatabaseBackup Backup() => new("Host=x;Database=y;Username=z;Password=p", _dir, NullLogger.Instance, pgDump: "aethera-no-such-pg-dump");

    private void File_(string name, DateTime at)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, at);
    }

    [Theory]
    [InlineData("aethera-scheduled-20261004-120000.dump", "scheduled")]
    [InlineData("aethera-pre-migration-20261004-120000.dump", "pre-migration")]
    [InlineData("aethera-manual-20261004-120000.dump", "manual")]
    [InlineData("whatever.dump", "unknown")]
    public void The_kind_is_read_from_the_file_name(string name, string kind) => Assert.Equal(kind, DatabaseBackup.KindOf(name));

    [Fact]
    public void Lists_newest_first_and_ignores_partial_files()
    {
        File_("aethera-scheduled-20261001-000000.dump", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        File_("aethera-scheduled-20261003-000000.dump", new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        File_("aethera-scheduled-20261004-000000.dump.partial", new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));
        File_("notes.txt", new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));

        var list = Backup().List();

        Assert.Equal(["aethera-scheduled-20261003-000000.dump", "aethera-scheduled-20261001-000000.dump"], list.Select(b => b.Name));
    }

    [Fact]
    public void Prune_keeps_the_newest_of_one_kind_and_leaves_other_kinds_alone()
    {
        for (var day = 1; day <= 5; day++) File_($"aethera-scheduled-2026100{day}-000000.dump", new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc));
        File_("aethera-pre-migration-20260901-000000.dump", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var deleted = Backup().Prune("scheduled", keep: 2);

        Assert.Equal(3, deleted);
        Assert.Equal(["aethera-scheduled-20261005-000000.dump", "aethera-scheduled-20261004-000000.dump", "aethera-pre-migration-20260901-000000.dump"],
            Backup().List().Select(b => b.Name));
    }

    [Fact]
    public void Prune_never_deletes_everything()
    {
        File_("aethera-scheduled-20261001-000000.dump", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, Backup().Prune("scheduled", keep: 0));
        Assert.Single(Backup().List());
    }

    [Fact]
    public void A_missing_pg_dump_is_reported_as_unavailable_not_as_an_exception()
    {
        Assert.False(Backup().IsAvailable());
    }

    [Fact]
    public async Task Backing_up_without_pg_dump_fails_with_a_clear_message_and_leaves_no_file()
    {
        var ex = await Assert.ThrowsAsync<BackupFailedException>(() => Backup().BackupAsync("manual", DateTimeOffset.UtcNow, default));
        Assert.Contains("pg_dump", ex.Message);
        Assert.Empty(Backup().List());
    }
}

public sealed class AgentUpdateAssetTests
{
    [Theory]
    [InlineData("amd64", "aethera-agent-linux-amd64")]
    [InlineData("x86_64", "aethera-agent-linux-amd64")]
    [InlineData("AMD64", "aethera-agent-linux-amd64")]
    [InlineData("arm64", "aethera-agent-linux-arm64")]
    [InlineData("aarch64", "aethera-agent-linux-arm64")]
    [InlineData("riscv64", null)]
    [InlineData(null, null)]
    public void Maps_the_reported_architecture_to_the_release_asset(string? architecture, string? asset) =>
        Assert.Equal(asset, AgentUpdateJobHandler.AssetName(architecture));
}
