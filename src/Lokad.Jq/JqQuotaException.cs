using System;

namespace Lokad.Jq;

// A resource-policy failure (budgets and output limits). Unlike catchable
// evaluation errors, quota exhaustion propagates through suppression and
// is always reported with status 5, no matter which stage was active.
internal sealed class JqQuotaException(string message) : JqException(message);
