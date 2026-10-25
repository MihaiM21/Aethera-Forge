using System.Diagnostics.CodeAnalysis;
using Cronos;

namespace Aethera.Infrastructure.Scheduling;

/// <summary>
/// A five-field cron expression in an IANA time zone (backup policies and scheduled tasks, ADR 0010). Daylight-saving gaps and overlaps
/// follow Cronos: a skipped local time runs once right after the gap, a repeated one runs once.
/// </summary>
public sealed class CronSchedule
{
    private readonly CronExpression _expression;

    private CronSchedule(string expression, CronExpression parsed, TimeZoneInfo timeZone)
    {
        Expression = expression;
        _expression = parsed;
        TimeZone = timeZone;
    }

    public string Expression { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>Parses <paramref name="expression"/> (five fields, macros like <c>@daily</c> allowed) in <paramref name="timeZoneId"/>.</summary>
    public static bool TryParse(string? expression, string? timeZoneId, [NotNullWhen(true)] out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "The schedule is required.";
            return false;
        }

        if (!TryFindTimeZone(string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId, out var timeZone))
        {
            error = $"Unknown time zone '{timeZoneId}'. Use an IANA name such as Europe/Bucharest.";
            return false;
        }

        try
        {
            var trimmed = expression.Trim();
            schedule = new CronSchedule(trimmed, CronExpression.Parse(trimmed, CronFormat.Standard), timeZone);
            error = null;
            return true;
        }
        catch (CronFormatException ex)
        {
            error = $"Invalid cron expression: {ex.Message}";
            return false;
        }
    }

    public static CronSchedule Parse(string expression, string? timeZoneId = null) =>
        TryParse(expression, timeZoneId, out var schedule, out var error) ? schedule : throw new FormatException(error);

    /// <summary>The first occurrence strictly after <paramref name="after"/>, or null when there is none.</summary>
    public DateTimeOffset? Next(DateTimeOffset after) => _expression.GetNextOccurrence(after, TimeZone)?.ToUniversalTime();

    /// <summary>True when the schedule fires more often than every <paramref name="interval"/> somewhere in the next day (abuse guard).</summary>
    public bool FiresMoreOftenThan(TimeSpan interval, DateTimeOffset from)
    {
        var previous = Next(from);
        var end = from.AddDays(1);
        while (previous is { } p && p < end)
        {
            var next = Next(p);
            if (next is { } n && n - p < interval) return true;
            previous = next;
        }

        return false;
    }

    private static bool TryFindTimeZone(string id, [NotNullWhen(true)] out TimeZoneInfo? timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            timeZone = null;
            return false;
        }
    }
}
