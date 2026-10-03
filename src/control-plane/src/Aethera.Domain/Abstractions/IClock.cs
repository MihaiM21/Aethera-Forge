namespace Aethera.Domain;

/// <summary>Source of the current time, so time-dependent code is testable. All values are UTC.</summary>
public interface IClock
{
    /// <summary>The current instant, with a zero offset.</summary>
    DateTimeOffset UtcNow { get; }
}
