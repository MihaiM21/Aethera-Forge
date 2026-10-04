using System.Collections.Concurrent;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Ssh;

/// <summary>Problem codes of the SSH transport (next to <see cref="TransportErrors"/>).</summary>
public static class SshErrors
{
    /// <summary>The server presented another host key than the pinned one; the connection stays blocked until a user confirms.</summary>
    public const string HostKeyChanged = "ssh.host_key_changed";

    /// <summary>The server refused the stored credentials.</summary>
    public const string AuthFailed = "ssh.auth_failed";

    /// <summary>The server has no SSH credential.</summary>
    public const string NoCredential = "ssh.no_credential";
}

/// <summary>A held channel slot on a server's connection; dispose to release it.</summary>
public sealed class SshLease : IDisposable
{
    private readonly Action _release;
    private readonly Action _broken;
    private int _released;

    internal SshLease(ISshConnection connection, SshServerAccess access, Action release, Action broken)
    {
        Connection = connection;
        Access = access;
        _release = release;
        _broken = broken;
    }

    public ISshConnection Connection { get; }

    public SshServerAccess Access { get; }

    /// <summary>The session failed under a command: the next caller connects anew.</summary>
    public void MarkBroken() => _broken();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release();
    }
}

/// <summary>
/// One multiplexed SSH connection per server with keep-alive, a per-server limit on concurrent channels and TOFU host key handling
/// (ADR 0002 "Connection pool"). Connecting is serialized per server; commands then run in parallel on separate channels.
/// </summary>
public sealed class SshConnectionPool(
    SshAccessProvider accessProvider, ISshConnector connector, SshHostKeyService hostKeys, IOptions<SshOptions> options, TimeProvider time, ILogger<SshConnectionPool> logger)
    : IAsyncDisposable
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim ConnectGate = new(1, 1);
        public SemaphoreSlim? Channels;
        public ISshConnection? Connection;
        public string? Key;
        public DateTimeOffset LastUsed;
        public int InFlight;
        public DateTimeOffset? FailedAt;
        public string? FailureReason;
        public string? FailureCode;
        public string? BlockedOn; // key the connection is blocked on until it changes
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly SshOptions _options = options.Value;

    /// <summary>Cheap, no I/O: can a command be tried now? A server in connect back-off or with an unconfirmed host key change is down.</summary>
    public (bool Available, string? Reason) Peek(Guid serverId)
    {
        if (!_entries.TryGetValue(serverId, out var entry)) return (true, null);
        if (entry.BlockedOn is not null) return (false, "The SSH host key changed and has not been confirmed.");
        if (entry.FailedAt is { } failedAt && time.GetUtcNow() - failedAt < TimeSpan.FromSeconds(_options.ConnectFailureBackoffSeconds))
            return (false, entry.FailureReason ?? "The last SSH connection attempt failed.");
        return (true, null);
    }

    /// <summary>Forget what the pool knows about a server (credentials, endpoint or host key changed): the next use connects anew.</summary>
    public async Task EvictAsync(Guid serverId)
    {
        accessProvider.Invalidate(serverId);
        if (_entries.TryRemove(serverId, out var entry) && entry.Connection is { } connection) await connection.DisposeAsync();
    }

    /// <param name="countChannel">False for long-lived follow streams, which must not starve ordinary commands of channel slots.</param>
    public async Task<SshLease> AcquireAsync(Guid serverId, CancellationToken cancellationToken, bool countChannel = true)
    {
        var entry = _entries.GetOrAdd(serverId, _ => new Entry { Channels = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentChannelsPerServer)) });
        SshServerAccess access;

        await entry.ConnectGate.WaitAsync(cancellationToken);
        try
        {
            // Read inside the gate: a caller that waited behind the first connect must see the host key that connect just pinned.
            try
            {
                access = await accessProvider.GetAsync(serverId, cancellationToken)
                    ?? throw new ServerTransportException(SshErrors.NoCredential, "The server has no SSH credential.");
            }
            catch (SshConnectException ex)
            {
                throw new ServerTransportException(SshErrors.AuthFailed, ex.Message); // the stored credential cannot be used (undecryptable)
            }
            if (entry.BlockedOn is not null)
            {
                if (entry.BlockedOn == access.PinnedFingerprint) throw HostKeyBlocked(); // the pin did not change since: still waiting for the user
                entry.BlockedOn = null;
            }

            var alive = entry.Connection is { IsConnected: true } && entry.Key == access.ConnectionKey;
            if (!alive)
            {
                if (entry.Connection is { } stale)
                {
                    entry.Connection = null;
                    await stale.DisposeAsync();
                }

                if (entry.FailedAt is { } failedAt && time.GetUtcNow() - failedAt < TimeSpan.FromSeconds(_options.ConnectFailureBackoffSeconds) && entry.Key == access.ConnectionKey)
                    throw new ServerTransportException(entry.FailureCode ?? TransportErrors.Unreachable, entry.FailureReason ?? "The last SSH connection attempt failed.");

                await ConnectAsync(serverId, access, entry, cancellationToken);
            }
        }
        finally
        {
            entry.ConnectGate.Release();
        }

        if (countChannel) await entry.Channels!.WaitAsync(cancellationToken);
        Interlocked.Increment(ref entry.InFlight);
        var connection = entry.Connection!;
        entry.LastUsed = time.GetUtcNow();
        return new SshLease(connection, access,
            release: () =>
            {
                entry.LastUsed = time.GetUtcNow();
                Interlocked.Decrement(ref entry.InFlight);
                if (countChannel) entry.Channels!.Release();
            },
            broken: () =>
            {
                if (ReferenceEquals(entry.Connection, connection)) entry.Key = null; // not alive for the next caller
            });
    }

    private async Task ConnectAsync(Guid serverId, SshServerAccess access, Entry entry, CancellationToken cancellationToken)
    {
        entry.Key = access.ConnectionKey;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds + 5));
            var settings = new SshConnectionSettings(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds), TimeSpan.FromSeconds(_options.KeepAliveSeconds));
            var connection = await connector.ConnectAsync(access.Target, access.Auth, access.PinnedFingerprint, settings, timeout.Token);
            if (access.PinnedFingerprint is null && await hostKeys.PinOnFirstUseAsync(serverId, connection.HostKey, cancellationToken))
                entry.Key = (access with { PinnedFingerprint = connection.HostKey.Fingerprint }).ConnectionKey; // the pin we just stored must not look like a change
            entry.Connection = connection;
            entry.FailedAt = null;
            entry.FailureReason = null;
            entry.FailureCode = null;
            logger.LogDebug("SSH connected to server {ServerId}", serverId);
        }
        catch (SshHostKeyChangedException changed)
        {
            entry.BlockedOn = access.PinnedFingerprint;
            await hostKeys.RecordChangedAsync(serverId, changed.PinnedFingerprint, changed.Presented, cancellationToken);
            logger.LogWarning("The SSH host key of server {ServerId} changed; connections are blocked until a user confirms it", serverId);
            throw HostKeyBlocked();
        }
        catch (SshConnectException ex)
        {
            Fail(entry, ex.Authentication ? SshErrors.AuthFailed : TransportErrors.Unreachable, ex.Message);
            logger.LogWarning("SSH connection to server {ServerId} failed: {Reason}", serverId, ex.Message);
            throw new ServerTransportException(entry.FailureCode!, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Fail(entry, TransportErrors.Unreachable, "The SSH connection timed out.");
            throw new ServerTransportException(TransportErrors.Unreachable, "The SSH connection timed out.");
        }
    }

    private void Fail(Entry entry, string code, string reason)
    {
        entry.FailedAt = time.GetUtcNow();
        entry.FailureCode = code;
        entry.FailureReason = reason;
    }

    private static ServerTransportException HostKeyBlocked() =>
        new(SshErrors.HostKeyChanged, "The SSH host key of this server changed. Confirm the new fingerprint before Aethera connects again.");

    /// <summary>Closes connections nobody used for <see cref="SshOptions.IdleDisconnectSeconds"/>.</summary>
    public async Task CloseIdleAsync()
    {
        var limit = TimeSpan.FromSeconds(_options.IdleDisconnectSeconds);
        var now = time.GetUtcNow();
        foreach (var (serverId, entry) in _entries)
        {
            if (entry.Connection is null || Volatile.Read(ref entry.InFlight) > 0 || now - entry.LastUsed < limit) continue;
            if (!await entry.ConnectGate.WaitAsync(0)) continue;
            try
            {
                if (entry.Connection is { } connection && Volatile.Read(ref entry.InFlight) == 0)
                {
                    entry.Connection = null;
                    entry.Key = null;
                    await connection.DisposeAsync();
                    logger.LogDebug("Closed the idle SSH connection of server {ServerId}", serverId);
                }
            }
            finally
            {
                entry.ConnectGate.Release();
            }
        }
    }

    public int OpenConnections => _entries.Values.Count(e => e.Connection is { IsConnected: true });

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values)
            if (entry.Connection is { } connection) await connection.DisposeAsync();
        _entries.Clear();
    }
}
