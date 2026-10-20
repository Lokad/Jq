using System;

namespace Lokad.Jq;

// A resource failure (admission and structural/regex limits). Unlike catchable
// evaluation errors, quota exhaustion propagates through suppression and
// is always reported with status 5, no matter which stage was active.
internal sealed class JqQuotaException(string message) : JqException(message)
{
    // Binding shape failures keep the existing argument-error category;
    // host admission refusal remains terminal across that boundary.
    internal bool IsMemoryAdmission { get; init; }
}
