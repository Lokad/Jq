using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// Shared boundary between catchable evaluation failures and terminal
// signals. Quota exhaustion, compile errors, cancellation, and host
// failures and actual allocation failures always propagate. Known language
// failures are catchable by optional suppression, pattern alternation and try/catch.
internal static class JqErrors
{
    internal static JsonNode? Payload(Exception exception, JqBudget budget)
    {
        if (exception is JqErrorException user) return user.Payload;
        if (exception is JqBreakException broken)
        {
            // Only an observing catch needs a JSON representation of the label.
            budget.ReserveBytes(224);
            return new JsonObject { ["__jq"] = JsonValue.Create((long)broken.Target.LabelId) };
        }
        return JsonValue.Create(exception.Message);
    }

    internal static bool IsCatchableClr(Exception exception) =>
        exception is JsonException or FormatException or ArgumentException or OverflowException;

    internal static bool IsCatchable(Exception exception) =>
        IsCatchableClr(exception)
        || (exception is JqException && exception is not JqQuotaException
            && exception is not JqCompileException && exception is not JqCloseException);
}
