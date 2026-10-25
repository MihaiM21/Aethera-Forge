namespace Aethera.Api.Tests.Backups;

/// <summary>IANA ids (Europe/Bucharest) resolve in the Linux container; a Windows machine with invariant globalization cannot, and these tests then skip.</summary>
public sealed class IanaTimeZoneFactAttribute : FactAttribute
{
    public IanaTimeZoneFactAttribute()
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
        }
        catch (TimeZoneNotFoundException)
        {
            Skip = "This machine cannot resolve IANA time zone ids (Windows with invariant globalization); they resolve in the Linux container.";
        }
    }
}
