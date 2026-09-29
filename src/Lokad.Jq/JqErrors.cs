using System;
using System.Text.Json;

namespace Lokad.Jq;

// Shared boundary between catchable evaluation failures and terminal
// signals. Quota exhaustion, compile errors, cancellation, and host
// failures always propagate; everything else is catchable by user code
// (optional suppression, pattern alternation, and later try/catch).
internal static class JqErrors
{
    internal static bool IsCatchable(Exception exception) =>
        exception is JsonException or FormatException or ArgumentException or OverflowException
        || (exception is JqException && exception is not JqQuotaException && exception is not JqCompileException);
}
