using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class CaptureFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            await foreach (JsonNode? value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
            {
                JqMatch.SplitArgument(context, value, out JsonNode? pattern, out JsonNode? modifiers);
                foreach (JsonObject match in JqMatch.Search(context, input, pattern, modifiers))
                    yield return JqMatch.ToNamedObject(context, match);
            }
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        await foreach (JsonNode? flags in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                foreach (JsonObject match in JqMatch.Search(context, input, pattern, flags))
                    yield return JqMatch.ToNamedObject(context, match);
    }
}
