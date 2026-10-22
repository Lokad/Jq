using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// Pass-through with stderr side effects: debug/0 logs the input as a
// DEBUG array, while debug/1 logs each message value and drops them.
// Bytes queue in the context; the executor drains them to the host.
internal sealed class DebugFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 0)
        {
            EmitDebug(context, input);
            yield return input;
            yield break;
        }
        await foreach (JsonNode? message in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
            EmitDebug(context, message);
        yield return input;
    }

    private static void EmitDebug(JqContext context, JsonNode? value)
    {
        var line = new JsonArray(JsonValue.Create("DEBUG:"), context.Budget.CopyValue(value, 1));
        using var bytes = context.Runtime.SerializeUtf8(line, context.AsciiOutput, null, false, context.SortKeys);
        context.EmitStderr(bytes.Value, true);
    }
}
