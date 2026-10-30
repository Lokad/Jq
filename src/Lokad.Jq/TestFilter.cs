using System.Collections.Generic;

namespace Lokad.Jq;

internal sealed class TestFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            await foreach (var value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
            {
                JqMatch.SplitArgument(context, value, out JqValue? pattern, out JqValue? flags);
                yield return JqScalar.Create(JqMatch.Test(context, input, pattern, flags));
            }
        }
        else
        {
            // jq evaluates the flags stream before the pattern stream.
            await foreach (var flags in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (var pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                yield return JqScalar.Create(JqMatch.Test(context, input, pattern, flags));
        }

    }
}
