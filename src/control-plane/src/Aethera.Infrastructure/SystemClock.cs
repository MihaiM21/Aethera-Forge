using Aethera.Domain;

namespace Aethera.Infrastructure;

/// <summary><see cref="IClock"/> over <see cref="TimeProvider"/> (the system clock unless a test substitutes one).</summary>
public sealed class SystemClock(TimeProvider? timeProvider = null) : IClock
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public DateTimeOffset UtcNow => _time.GetUtcNow();
}
