using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class MatchFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count is not (1 or 2))
            throw new JqException("match expects one or two arguments");
        if (args.Count == 1)
        {
            foreach (JsonNode? value in args[0].Evaluate(input, context, environment))
            {
                JqMatch.SplitArgument(context, value, out JsonNode? pattern, out JsonNode? modifiers);
                foreach (JsonObject match in JqMatch.Search(context, input, pattern, modifiers))
                    yield return match;
            }
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        foreach (JsonNode? flags in args[1].Evaluate(input, context, environment))
            foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment))
                foreach (JsonObject match in JqMatch.Search(context, input, pattern, flags))
                    yield return match;
    }
}

// _match_impl(regex; modifiers; testmode): upstream test flag is exact-true for boolean path, otherwise array path.
internal sealed class MatchImplFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (args.Count != 3)
            throw new JqException("_match_impl expects three arguments");
        foreach (JsonNode? testmode in args[2].Evaluate(input, context, environment))
        foreach (JsonNode? modifiers in args[1].Evaluate(input, context, environment))
        foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment))
        {
            bool isTest = testmode is JsonValue testValue && testValue.TryGetValue<bool>(out bool flag) && flag;
            List<JsonObject> matches = JqMatch.Search(context, input, pattern, modifiers);
            if (isTest)
            {
                yield return JsonValue.Create(matches.Count > 0);
            }
            else
            {
                var array = new JsonArray();
                context.Budget.ChargeNode();
                foreach (JsonObject match in matches)
                {
                    context.Budget.ChargeNode();
                    array.Add(match);
                }
                yield return array;
            }
        }
    }
}
