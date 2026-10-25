using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aethera.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.SelfOps;

public sealed record ReleaseAsset(string Name, string Url, long SizeBytes);

/// <summary>A published release. <see cref="Version"/> is the semantic version without the <c>v</c>.</summary>
public sealed record ReleaseInfo(string Version, string Tag, string Url, string? Notes, bool PreRelease, DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Looks for newer releases of Aethera on GitHub and remembers the answer. A failed check never throws and never forgets the last good
/// answer: an air-gapped or rate-limited installation just keeps showing "checked at ..." with the error.
/// </summary>
public sealed class ReleaseChecker(HttpClient http, IOptions<UpdateOptions> options, IClock clock, ILogger<ReleaseChecker> logger)
{
    private readonly object _gate = new();
    private ReleaseInfo? _latest;
    private DateTimeOffset? _checkedAt;
    private string? _error;

    public string CurrentVersion { get; init; } = AppVersion.Current;

    public ReleaseInfo? Latest { get { lock (_gate) return _latest; } }
    public DateTimeOffset? CheckedAt { get { lock (_gate) return _checkedAt; } }
    public string? LastError { get { lock (_gate) return _error; } }
    public bool Enabled => options.Value.Enabled;

    /// <summary>True when <see cref="Latest"/> has higher precedence than the running version.</summary>
    public bool UpdateAvailable
    {
        get
        {
            var latest = Latest;
            return latest is not null
                && SemanticVersion.TryParse(latest.Version, out var l)
                && SemanticVersion.TryParse(CurrentVersion, out var c)
                && l > c;
        }
    }

    public async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return Latest;
        try
        {
            var found = await FetchAsync(settings, cancellationToken);
            lock (_gate)
            {
                _latest = found;
                _checkedAt = clock.UtcNow;
                _error = null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            // The message names the host and status, never a token (none is sent).
            logger.LogWarning("Update check failed: {Message}", ex.Message);
            lock (_gate)
            {
                _checkedAt = clock.UtcNow;
                _error = ex.Message;
            }
        }
        return Latest;
    }

    private async Task<ReleaseInfo?> FetchAsync(UpdateOptions settings, CancellationToken cancellationToken)
    {
        var repo = settings.Repository.Trim('/');
        if (repo.Count(c => c == '/') != 1 || repo.Any(char.IsWhiteSpace)) throw new InvalidOperationException($"Aethera:Updates:Repository must be 'owner/name', not '{settings.Repository}'.");
        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var url = settings.IncludePrereleases ? $"{baseUrl}/repos/{repo}/releases?per_page=20" : $"{baseUrl}/repos/{repo}/releases/latest";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Aethera", CurrentVersion));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no release published yet
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} for the releases of {repo}.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var candidates = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(Parse).OfType<ReleaseInfo>()
            : Parse(document.RootElement) is { } single ? [single] : [];
        return candidates.OrderByDescending(r => SemanticVersion.Parse(r.Version)).FirstOrDefault();
    }

    private static ReleaseInfo? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        var tag = Str(e, "tag_name");
        if (tag is null || !SemanticVersion.TryParse(tag, out var version)) return null;
        var assets = new List<ReleaseAsset>();
        if (e.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = Str(a, "name");
                var assetUrl = Str(a, "browser_download_url");
                if (name is null || assetUrl is null) continue;
                assets.Add(new ReleaseAsset(name, assetUrl, a.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
            }
        }
        DateTimeOffset? published = Str(e, "published_at") is { } p && DateTimeOffset.TryParse(p, out var when) ? when : null;
        return new ReleaseInfo(
            version.ToString(), tag, Str(e, "html_url") ?? "", Str(e, "body"),
            e.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True || version.IsPreRelease, published, assets);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The SHA-256 (hex) the release's <c>SHA256SUMS</c> lists for <paramref name="assetName"/>, or null when it has none.</summary>
    public async Task<string?> GetChecksumAsync(ReleaseInfo release, string assetName, CancellationToken cancellationToken)
    {
        var sums = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS");
        if (sums is null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, sums.Url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Aethera", CurrentVersion));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == assetName && parts[0].Length == 64) return parts[0].ToLowerInvariant();
        }
        return null;
    }
}

/// <summary>Checks for a new release shortly after start and then every <see cref="UpdateOptions.IntervalHours"/>.</summary>
public sealed class UpdateCheckService(ReleaseChecker checker, IOptions<UpdateOptions> options, ILogger<UpdateCheckService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || AetheraHost.IsOpenApiGeneration) return;
        try
        {
            // Not at the very first second: start-up has better things to do, and a crash loop must not hammer GitHub.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(options.Value.Interval);
            do
            {
                var latest = await checker.CheckAsync(stoppingToken);
                if (checker.UpdateAvailable) logger.LogInformation("Aethera {Latest} is available (running {Current})", latest!.Version, checker.CurrentVersion);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
