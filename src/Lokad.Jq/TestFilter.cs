using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

internal sealed class TestFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            foreach (var value in args[0].Evaluate(input, context, environment))
            {
                JqMatch.SplitArgument(context, value, out JsonNode? pattern, out JsonNode? flags);
                yield return Match(pattern, flags);
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
            string text = JqMatch.RequireText(context, input);
            string patternText = JqMatch.RequirePattern(context, pattern);
            JqRegexOptions options = JqRegexOptions.ParseOrThrow(JqMatch.RequireModifiers(context, flags));
            var regex = context.Regexes.Get(patternText, options.Pattern);
            using var subject = context.Regexes.EncodeSubject(text);
            return JsonValue.Create(context.Regexes.IsMatch(regex, subject.Value, options.Match));
        }
    }
}
