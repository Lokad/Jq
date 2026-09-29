using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

internal sealed class TestFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count is not (1 or 2))
            throw new JqException("test expects one or two arguments");
        if (args.Count == 1)
        {
            foreach (var value in args[0].Evaluate(input, context, environment))
            {
                if (value is JsonArray array)
                    yield return Match(array.Count > 0 ? array[0] : null, array.Count > 1 ? array[1] : null);
                else
                    yield return Match(value, null);
            }
        }
        else
        {
            // jq evaluates the flags stream before the pattern stream.
            foreach (var flags in args[1].Evaluate(input, context, environment))
            foreach (var pattern in args[0].Evaluate(input, context, environment))
                yield return Match(pattern, flags);
        }

        JsonNode Match(JsonNode? pattern, JsonNode? flags)
        {
            var text = String(input);
            if (!JqRegexOptions.TryParse(flags == null ? string.Empty : String(flags), out var options, out var unsupportedFlag))
                throw new JqException($"unsupported test flag '{unsupportedFlag}'");
            var regex = context.Regexes.Get(String(pattern), options.Pattern);
            return JsonValue.Create(context.Regexes.Match(regex, text, 0, options.Match).Success);
        }
    }
}
