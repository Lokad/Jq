using System;

namespace Lokad.Jq;

// A host violated its byte/descriptor contract (bad sizes, no progress,
// or an unexpected CLR failure from IJqHost). This escapes the jq error
// boundary instead of being reported as a filter, input, or usage error.
internal sealed class JqHostFailureException(string message, Exception? inner) : Exception(message, inner);
