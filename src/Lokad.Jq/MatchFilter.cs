using System;
using System.Collections.Generic;

namespace Lokad.Jq;

internal sealed class MatchFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            await foreach (JqValue? value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
            {
                JqMatch.SplitArgument(context, value, out JqValue? pattern, out JqValue? modifiers);
                foreach (JqValue match in JqMatch.Search(context, input, pattern, modifiers))
                    yield return match;
            }
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        await foreach (JqValue? flags in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JqValue? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                foreach (JqValue match in JqMatch.Search(context, input, pattern, flags))
                    yield return match;
    }
}

// _match_impl(regex; modifiers; testmode): upstream test flag is exact-true for boolean path, otherwise array path.
internal sealed class MatchImplFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JqValue? testmode in args[2].Evaluate(input, context, environment).ConfigureAwait(false))
        await foreach (JqValue? modifiers in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
        await foreach (JqValue? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
        {
            bool isTest = testmode is JqScalar testValue && testValue.TryGetValue<bool>(out bool flag) && flag;
            if (isTest)
            {
                yield return JqScalar.Create(JqMatch.Test(context, input, pattern, modifiers));
            }
            else
            {
                List<JqValue> matches = JqMatch.Search(context, input, pattern, modifiers);
                context.Budget.ReserveMetadata(64 + 24L * matches.Count, 1);
                var array = new JqArray();
                context.Budget.CheckCancellation();
                foreach (JqValue match in matches)
                {
                    context.Budget.CheckCancellation();
                    array.Add(match);
                }
                yield return array;
            }
        }
    }
}
