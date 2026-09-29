using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class ScanFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count is not (1 or 2))
            throw new JqException("scan expects one or two arguments");
        if (args.Count == 1)
        {
            foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment))
                foreach (JsonNode? value in JqMatch.Scan(context, input, pattern, null))
                    yield return value;
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        foreach (JsonNode? flags in args[1].Evaluate(input, context, environment))
            foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment))
                foreach (JsonNode? value in JqMatch.Scan(context, input, pattern, flags))
                    yield return value;
    }
}
