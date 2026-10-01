using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class SplitsFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    // Splits emit fresh substrings, so results travel untracked.
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count is not (1 or 2))
            throw new JqException("splits expects one or two arguments");
        if (args.Count == 1)
        {
            foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment))
                foreach (string piece in JqMatch.SplitPieces(context, input, pattern, null))
                {
                    yield return JsonValue.Create(piece);
                }
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        foreach (JsonNode? flags in args[1].Evaluate(input, context, environment))
            foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment))
                foreach (string piece in JqMatch.SplitPieces(context, input, pattern, flags))
                {
                    yield return JsonValue.Create(piece);
                }
    }
}
