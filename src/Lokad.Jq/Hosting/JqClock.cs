using System;

namespace Lokad.Jq;

/// <summary>
/// An explicit clock and local time zone supplied by the host for a jq command.
/// The library never reads the machine clock or timezone on its own; date builtins
/// use these values, so repeated executions with the same clock stay deterministic.
/// A null clock on the invocation means the capability is absent and time builtins
/// report an explicit diagnostic instead.
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
