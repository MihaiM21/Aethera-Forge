using System.Text.Json;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Ssh;

/// <summary>Per-server switch of the SSH fallback (<c>allowSshFallback</c>); absent means allowed.</summary>
public sealed record SshFallbackSetting(bool Allow);

/// <summary>A host key that differs from the pinned one, waiting for a user's decision.</summary>
public sealed record PendingHostKey(string Algorithm, string Fingerprint, DateTimeOffset SeenAt);

/// <summary>State of the SSH polling mode of a server (the "degraded: polling over SSH" status).</summary>
public sealed record SshPollingState(DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt, string? LastError, DateTimeOffset? Since);

/// <summary>
/// Small JSON documents in <c>settings</c> under the reserved <c>ssh.</c> prefix. No table of its own: the data is tiny, per server, and
/// only the SSH transport reads it (the same approach as <c>agent.discovery.*</c>).
/// </summary>
public sealed class SshSettingsStore(IServiceScopeFactory scopes, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string FallbackKey(Guid serverId) => $"ssh.fallback.{serverId:D}";

    public static string PendingHostKeyKey(Guid serverId) => $"ssh.hostkey.pending.{serverId:D}";

    public static string PollingKey(Guid serverId) => $"ssh.polling.{serverId:D}";

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(setting.ValueJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken) where T : class
    {
        var json = JsonSerializer.Serialize(value, Json);
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
            if (setting is null) db.Settings.Add(new InstanceSetting { Key = key, ValueJson = json, UpdatedAt = clock.UtcNow });
            else { setting.ValueJson = json; setting.UpdatedAt = clock.UtcNow; }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Two writers inserted the same key at once: the second round updates it.
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        await db.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<bool> FallbackAllowedAsync(Guid serverId, CancellationToken cancellationToken) =>
        (await GetAsync<SshFallbackSetting>(FallbackKey(serverId), cancellationToken))?.Allow ?? true;
}
