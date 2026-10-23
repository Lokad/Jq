using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class ScanFilter(IReadOnlyList<JqFilter> args) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            await foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                foreach (JsonNode? value in JqMatch.Scan(context, input, pattern, null))
                    yield return value;
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        await foreach (JsonNode? flags in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                foreach (JsonNode? value in JqMatch.Scan(context, input, pattern, flags))
                    yield return value;
    }
}
