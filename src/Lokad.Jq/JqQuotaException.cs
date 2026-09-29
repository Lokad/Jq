using System;

namespace Lokad.Jq;

// A resource-policy failure (budgets and output limits). Unlike catchable
// evaluation errors, quota exhaustion propagates through suppression and
// stays mapped to its execution stage.
internal sealed class JqQuotaException(string message) : JqException(message);
