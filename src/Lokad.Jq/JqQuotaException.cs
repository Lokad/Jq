using System;

namespace Lokad.Jq;

// A resource failure (admission and structural limits). Unlike catchable
// evaluation errors, quota exhaustion propagates through suppression and
// is always reported with status 5, no matter which stage was active.
internal sealed class JqQuotaException(string message) : JqException(message)
{
    // Admission refusal uses the fixed emergency diagnostic when rendering
    // cannot reserve storage from the same shared budget.
    internal bool IsMemoryAdmission { get; init; }
}
