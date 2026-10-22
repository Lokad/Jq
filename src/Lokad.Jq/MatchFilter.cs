using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class MatchFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            await foreach (JsonNode? value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
            {
                JqMatch.SplitArgument(context, value, out JsonNode? pattern, out JsonNode? modifiers);
                foreach (JsonNode match in JqMatch.Search(context, input, pattern, modifiers))
                    yield return match;
            }
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        await foreach (JsonNode? flags in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                foreach (JsonNode match in JqMatch.Search(context, input, pattern, flags))
                    yield return match;
    }
}

// _match_impl(regex; modifiers; testmode): upstream test flag is exact-true for boolean path, otherwise array path.
internal sealed class MatchImplFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? testmode in args[2].Evaluate(input, context, environment).ConfigureAwait(false))
        await foreach (JsonNode? modifiers in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
        await foreach (JsonNode? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
        {
            bool isTest = testmode is JsonValue testValue && testValue.TryGetValue<bool>(out bool flag) && flag;
            if (isTest)
            {
                yield return JsonValue.Create(JqMatch.Test(context, input, pattern, modifiers));
            }
            else
            {
                List<JsonNode> matches = JqMatch.Search(context, input, pattern, modifiers);
                context.Budget.ReserveMetadata(64 + 24L * matches.Count, 1);
                var array = new JsonArray();
                context.Budget.CheckCancellation();
                foreach (JsonNode match in matches)
                {
                    context.Budget.CheckCancellation();
                    array.Add(match);
                }
                yield return array;
            }
        }
    }
}
