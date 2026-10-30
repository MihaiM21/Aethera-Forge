using System.Security.Cryptography;
using Npgsql;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// The per-server cap on parallel builds (<c>Server.MaxConcurrentBuilds</c>, ADR 0004): a build waits for one of the server's slots and holds
/// it for the duration of the Build step. A slot is a PostgreSQL session-level advisory lock on a connection the slot keeps open, so it works
/// across API instances and disappears by itself when the holder dies.
/// </summary>
public sealed class BuildSlotGate(IServiceProvider services)
{
    private NpgsqlDataSource DataSource => (NpgsqlDataSource)services.GetService(typeof(NpgsqlDataSource))!;

    /// <summary>How long a waiting build sleeps between looks at the slots.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits for a free slot among <paramref name="slots"/> on <paramref name="serverId"/> and returns it; dispose it to release the slot.
    /// <paramref name="onWaiting"/> is called once when every slot is taken.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(Guid serverId, int slots, Func<CancellationToken, ValueTask>? onWaiting, CancellationToken ct)
    {
        slots = Math.Clamp(slots, 1, 64);
        var connection = await DataSource.OpenConnectionAsync(ct);
        try
        {
            var announced = false;
            while (true)
            {
                for (var slot = 0; slot < slots; slot++)
                {
                    var key = KeyOf(serverId, slot);
                    await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
                    command.Parameters.Add(new NpgsqlParameter { Value = key });
                    if (await command.ExecuteScalarAsync(ct) is true) return new Slot(connection, key);
                }

                if (!announced && onWaiting is not null)
                {
                    announced = true;
                    await onWaiting(ct);
                }

                await Task.Delay(PollInterval, ct);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>A stable 64-bit key per (server, slot); advisory lock keys are one global namespace of the database.</summary>
    public static long KeyOf(Guid serverId, int slot)
    {
        Span<byte> input = stackalloc byte[20];
        serverId.TryWriteBytes(input);
        BitConverter.TryWriteBytes(input[16..], slot);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return BitConverter.ToInt64(hash[..8]);
    }

    private sealed class Slot(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
                command.Parameters.Add(new NpgsqlParameter { Value = key });
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // The connection goes back to the pool, which resets the session and with it the lock.
            }

            await connection.DisposeAsync();
        }
    }
}
