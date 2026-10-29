using System.Text.Json;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.SelfOps;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Deployments;

/// <summary>The instance's Let's Encrypt account (settings key <c>proxy</c>).</summary>
public sealed record AcmeSetting(string? AcmeEmail, bool Staging);

/// <summary>
/// The domain the Aethera UI and API are served on (settings key <c>dashboard</c>), routed by the proxy of <see cref="ServerId"/> (null = the
/// <c>local</c> server, the control plane's own machine).
/// </summary>
public sealed record DashboardSetting(string? Domain, bool Https, Guid? ServerId);

/// <summary>What the proxy settings are now: the stored rows, else the configuration they were seeded from (ADR 0011).</summary>
/// <param name="Stored">The value comes from Settings &gt; System rather than the installer's environment.</param>
public sealed record EffectiveProxySettings(AcmeSetting Acme, DashboardSetting Dashboard, bool Stored)
{
    public bool AcmeAvailable => !string.IsNullOrWhiteSpace(Acme.AcmeEmail);
}

/// <summary>
/// The proxy and control-plane domain settings in <c>settings</c> (keys <c>proxy</c> and <c>dashboard</c>). Until someone saves them in
/// Settings &gt; System they read as the installer's environment (<c>AETHERA_ACME_EMAIL</c>, <c>AETHERA_DOMAIN</c> through
/// <see cref="ProxyOptions"/>); after that the database wins, so the domain changes without editing the <c>.env</c>.
/// </summary>
public static class ProxySettingsStore
{
    public const string AcmeKey = "proxy";
    public const string DashboardKey = "dashboard";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<EffectiveProxySettings> GetAsync(AetheraDbContext db, ProxyOptions options, CancellationToken ct)
    {
        var rows = await db.Settings.AsNoTracking().Where(s => s.Key == AcmeKey || s.Key == DashboardKey).ToListAsync(ct);
        var acme = Read<AcmeSetting>(rows, AcmeKey);
        var dashboard = Read<DashboardSetting>(rows, DashboardKey);
        return new EffectiveProxySettings(
            acme ?? new AcmeSetting(Blank(options.AcmeEmail), options.Staging),
            dashboard ?? new DashboardSetting(Blank(options.ControlPlaneDomain)?.ToLowerInvariant(), options.ControlPlaneHttps, null),
            acme is not null || dashboard is not null);
    }

    public static async Task SaveAsync(AetheraDbContext db, AcmeSetting acme, DashboardSetting dashboard, Guid? userId, DateTimeOffset now, CancellationToken ct)
    {
        await UpsertAsync(db, AcmeKey, JsonSerializer.Serialize(acme, Json), userId, now, ct);
        await UpsertAsync(db, DashboardKey, JsonSerializer.Serialize(dashboard, Json), userId, now, ct);
    }

    /// <summary>
    /// The address people open the UI at, for links in emails and notifications: the domain saved in Settings &gt; System when there is one (it
    /// was set after installation, so it is newer than the <c>.env</c>), else <paramref name="configured"/> (<c>Aethera:PublicUrl</c>).
    /// </summary>
    public static async Task<string?> PublicUrlAsync(AetheraDbContext db, string? configured, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == DashboardKey, ct);
        var dashboard = row is null ? null : Read<DashboardSetting>([row], DashboardKey);
        return dashboard?.Domain is { Length: > 0 } domain ? $"{(dashboard.Https ? "https" : "http")}://{domain}" : configured;
    }

    /// <summary>The server whose proxy serves the control plane's domain: the configured one, else the <c>local</c> server; null when there is none.</summary>
    public static async Task<Guid?> DashboardServerIdAsync(AetheraDbContext db, DashboardSetting dashboard, CancellationToken ct)
    {
        if (dashboard.ServerId is { } id && await db.Servers.AnyAsync(s => s.Id == id, ct)) return id;
        return await db.Servers.AsNoTracking().Where(s => s.Name == LocalServerEnabler.ServerName)
            .OrderBy(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
    }

    private static async Task UpsertAsync(AetheraDbContext db, string key, string json, Guid? userId, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(new InstanceSetting { Key = key, ValueJson = json, UpdatedAt = now, UpdatedByUserId = userId });
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = now;
            row.UpdatedByUserId = userId;
        }
    }

    private static T? Read<T>(IEnumerable<InstanceSetting> rows, string key) where T : class
    {
        var row = rows.FirstOrDefault(r => r.Key == key);
        if (row is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(row.ValueJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
