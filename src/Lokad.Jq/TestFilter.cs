using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class TestFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            await foreach (var value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
            {
                JqMatch.SplitArgument(context, value, out JsonNode? pattern, out JsonNode? flags);
                yield return JsonValue.Create(JqMatch.Test(context, input, pattern, flags));
            }
        }
        else
        {
            // jq evaluates the flags stream before the pattern stream.
            await foreach (var flags in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (var pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                yield return JsonValue.Create(JqMatch.Test(context, input, pattern, flags));
        }

    }
}
