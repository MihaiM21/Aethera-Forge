namespace Aethera.Api.Tests.ScheduledTasks;

/// <summary>
/// Time zone ids that resolve on the machine running the tests. The projects set <c>InvariantGlobalization</c>, so a Windows machine (no ICU
/// lookup) only knows its own ids, while Linux (tzdata files, the production case) knows the IANA names.
/// </summary>
internal static class TestZones
{
    public static readonly string Bucharest = Pick("Europe/Bucharest", "E. Europe Standard Time");
    public static readonly string Tokyo = Pick("Asia/Tokyo", "Tokyo Standard Time");

    private static string Pick(string iana, string windows)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(iana);
            return iana;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return windows;
        }
    }
}
