using System.Globalization;

namespace Aethera.Infrastructure.Backups;

/// <summary>A successful, not yet pruned backup, as retention sees it.</summary>
public sealed record RetentionCandidate(Guid Id, DateTimeOffset At);

/// <summary>
/// The retention rule of ADR 0010: keep the newest successful run of each of the newest <c>keep_daily</c> days, <c>keep_weekly</c> ISO weeks and
/// <c>keep_monthly</c> months <em>that have a backup</em> (a server that was down for a week does not make older backups expire), and always the
/// newest successful run. Days, weeks and months are those of the policy's time zone. Failed, running and already pruned runs are not candidates.
/// </summary>
public static class BackupRetention
{
    /// <summary>The ids that retention removes from the store.</summary>
    public static IReadOnlyList<Guid> ToPrune(IReadOnlyCollection<RetentionCandidate> runs, int keepDaily, int keepWeekly, int keepMonthly, TimeZoneInfo timeZone)
    {
        if (runs.Count == 0) return [];
        var ordered = runs.OrderByDescending(r => r.At).ThenByDescending(r => r.Id).ToList();
        var keep = new HashSet<Guid> { ordered[0].Id };
        var local = ordered.Select(r => (Run: r, Time: TimeZoneInfo.ConvertTime(r.At, timeZone))).ToList();

        KeepNewestOfBuckets(local, keepDaily, t => t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), keep);
        KeepNewestOfBuckets(local, keepWeekly, t => $"{ISOWeek.GetYear(t.DateTime)}-W{ISOWeek.GetWeekOfYear(t.DateTime):00}", keep);
        KeepNewestOfBuckets(local, keepMonthly, t => t.ToString("yyyy-MM", CultureInfo.InvariantCulture), keep);

        return ordered.Where(r => !keep.Contains(r.Id)).Select(r => r.Id).ToList();
    }

    private static void KeepNewestOfBuckets(
        List<(RetentionCandidate Run, DateTimeOffset Time)> newestFirst, int count, Func<DateTimeOffset, string> bucketOf, HashSet<Guid> keep)
    {
        if (count <= 0) return;
        var seen = new HashSet<string>();
        foreach (var (run, time) in newestFirst)
        {
            var bucket = bucketOf(time);
            if (!seen.Add(bucket)) continue; // an older run of a bucket whose newest run was already taken
            keep.Add(run.Id);
            if (seen.Count >= count) return;
        }
    }
}
