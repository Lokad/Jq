using System;

namespace Lokad.Jq;

/// <summary>
/// An explicit clock and local time zone supplied by the host for a jq command.
/// Current-time builtins read the supplied clock on each call; local-time builtins
/// use the supplied time zone.
/// </summary>
public sealed class JqClock
{
    /// <summary>Initializes an explicit clock with its local time zone.</summary>
    public JqClock(TimeProvider clock, TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        Clock = clock;
        LocalTimeZone = localTimeZone;
    }

    /// <summary>Gets the clock read for current-time builtins.</summary>
    public TimeProvider Clock { get; }

    /// <summary>Gets the time zone used for local-time builtins.</summary>
    public TimeZoneInfo LocalTimeZone { get; }
}
