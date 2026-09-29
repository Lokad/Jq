using System;

namespace Lokad.Jq;

// Explicit host clock access for time builtins. The evaluator never reads
// the machine clock; a missing clock is an explicit diagnostic, and local
// decomposition helpers will read the accompanying time zone.
internal static class JqTime
{
    internal static double Now(JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Clock is null)
            throw new JqException("now requires an explicit host clock");
        return (context.Clock.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;
    }
}
