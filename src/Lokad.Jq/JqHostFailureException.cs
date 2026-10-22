using System;

namespace Lokad.Jq;

// An unexpected failure from a host capability, including byte IO, memory,
// clocks and adapted regex callbacks. This escapes the jq error
// boundary instead of being reported as a filter, input, or usage error.
internal sealed class JqHostFailureException(string message, Exception? inner) : Exception(message, inner);
