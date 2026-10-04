using Aethera.Domain;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Ssh;

/// <summary>Result of a user's decision about a changed host key.</summary>
public enum HostKeyConfirmation
{
    /// <summary>The fingerprint is now the pinned one.</summary>
    Pinned,

    /// <summary>Nothing is waiting and the fingerprint is not the pinned one.</summary>
    NothingToConfirm,

    /// <summary>The fingerprint the user confirmed is not the one the server presented last (the key changed again).</summary>
    Mismatch,
}

/// <summary>
/// Trust on first use for SSH host keys (ADR 0002): the first key a server presents is pinned (<c>servers.ssh_host_key_fingerprint</c>); a
/// different key later blocks the connection, is recorded as pending and only replaces the pin when a user confirms exactly that fingerprint.
/// </summary>
public sealed class SshHostKeyService(IServiceScopeFactory scopes, SshSettingsStore settings, SshAccessProvider access, AgentAudit audit, IClock clock)
{
    /// <summary>Pins <paramref name="observed"/> if the server has no pinned key yet. Returns false when another connection pinned a key first.</summary>
    public async Task<bool> PinOnFirstUseAsync(Guid serverId, HostKeyInfo observed, CancellationToken cancellationToken)
    {
        int changed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var now = clock.UtcNow;
            changed = await db.Servers.Where(s => s.Id == serverId && s.SshHostKeyFingerprint == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.SshHostKeyFingerprint, observed.Fingerprint).SetProperty(s => s.UpdatedAt, now), cancellationToken);
        }

        access.Invalidate(serverId);
        if (changed == 0) return false;
        await audit.RecordAsync("server.ssh_host_key_pinned", "server", serverId, new { algorithm = observed.Algorithm, fingerprint = observed.Fingerprint }, cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>A server presented a key other than the pinned one: remember it so the API can show it and a user can confirm.</summary>
    public async Task RecordChangedAsync(Guid serverId, string pinned, HostKeyInfo presented, CancellationToken cancellationToken)
    {
        var key = SshSettingsStore.PendingHostKeyKey(serverId);
        var existing = await settings.GetAsync<PendingHostKey>(key, cancellationToken);
        if (existing?.Fingerprint == presented.Fingerprint) return; // already known, no new audit event for every retry
        await settings.SetAsync(key, new PendingHostKey(presented.Algorithm, presented.Fingerprint, clock.UtcNow), cancellationToken);
        await audit.RecordAsync("server.ssh_host_key_changed", "server", serverId,
            new { algorithm = presented.Algorithm, pinned, presented = presented.Fingerprint }, cancellationToken: cancellationToken);
    }

    public Task<PendingHostKey?> GetPendingAsync(Guid serverId, CancellationToken cancellationToken) =>
        settings.GetAsync<PendingHostKey>(SshSettingsStore.PendingHostKeyKey(serverId), cancellationToken);

    /// <summary>
    /// Replaces the pinned key by <paramref name="fingerprint"/>, which must be the pending one (or, when no key is pinned yet, any key the
    /// user verified out of band). The caller audits.
    /// </summary>
    public async Task<(HostKeyConfirmation Result, string? Previous)> ConfirmAsync(Guid serverId, string fingerprint, CancellationToken cancellationToken)
    {
        var pending = await GetPendingAsync(serverId, cancellationToken);
        string? previous;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            previous = await db.Servers.AsNoTracking().Where(s => s.Id == serverId).Select(s => s.SshHostKeyFingerprint).FirstOrDefaultAsync(cancellationToken);

            if (pending is null && previous is not null) return previous == fingerprint ? (HostKeyConfirmation.Pinned, previous) : (HostKeyConfirmation.NothingToConfirm, previous);
            if (pending is not null && pending.Fingerprint != fingerprint) return (HostKeyConfirmation.Mismatch, previous);

            var now = clock.UtcNow;
            await db.Servers.Where(s => s.Id == serverId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.SshHostKeyFingerprint, fingerprint).SetProperty(s => s.UpdatedAt, now), cancellationToken);
        }

        await settings.DeleteAsync(SshSettingsStore.PendingHostKeyKey(serverId), cancellationToken);
        access.Invalidate(serverId);
        return (HostKeyConfirmation.Pinned, previous);
    }
}
