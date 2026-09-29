using System.Collections.Generic;
using System.Text.Json.Nodes;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

// Pass-through with stderr side effects: debug/0 logs the input as a
// DEBUG array, while debug/1 logs each message value and drops them.
// Bytes queue in the context; the executor drains them to the host.
internal sealed class DebugFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count > 1)
            throw new JqException("debug expects zero or one arguments");
        if (args.Count == 0)
        {
            EmitDebug(context, input);
            yield return context.Runtime.Clone(input);
            yield break;
        }
        foreach (JsonNode? message in args[0].Evaluate(input, context, environment))
            EmitDebug(context, message);
        yield return context.Runtime.Clone(input);
    }

    private static void EmitDebug(JqContext context, JsonNode? value)
    {
        var line = new JsonArray(JsonValue.Create("DEBUG:"), context.Runtime.Clone(value));
        byte[] bytes = ByteLines.AppendNewline(context.Runtime.SerializeUtf8(line, false, null, false)).ToArray();
        context.EmitStderr(bytes);
    }
}
