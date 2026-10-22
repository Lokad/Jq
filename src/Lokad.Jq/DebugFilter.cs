using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// Pass-through with stderr side effects: debug/0 logs the input as a
// DEBUG array, while debug/1 logs each message value and drops them.
// Await each host write before advancing the message producer.
internal sealed class DebugFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 0)
        {
            await EmitDebugAsync(context, input).ConfigureAwait(false);
            yield return input;
            yield break;
        }
        await foreach (JsonNode? message in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
            await EmitDebugAsync(context, message).ConfigureAwait(false);
        yield return input;
    }

    private static async ValueTask EmitDebugAsync(JqContext context, JsonNode? value)
    {
        using var bytes = context.Runtime.SerializeDebug(value, context.AsciiOutput, context.SortKeys);
        await context.EmitStderrAsync(bytes.Value).ConfigureAwait(false);
    }
}
