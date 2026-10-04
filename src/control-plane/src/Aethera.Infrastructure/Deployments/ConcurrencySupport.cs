using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Deployments;

internal static class ConcurrencySupport
{
    /// <summary>
    /// Applies <paramref name="apply"/> and saves. Other writers touch the same rows while a deployment runs (the API allocating the next
    /// deployment number, a status refresh), and the <c>xmin</c> token rejects the lost update; reload the conflicting rows and apply again.
    /// <paramref name="apply"/> must only assign properties, so that it can run more than once.
    /// </summary>
    public static async Task SaveWithRetryAsync(AetheraDbContext db, Action apply, CancellationToken ct, int retries = 5)
    {
        for (var attempt = 0; ; attempt++)
        {
            apply();
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < retries)
            {
                foreach (var entry in ex.Entries) await entry.ReloadAsync(ct);
            }
        }
    }
}
