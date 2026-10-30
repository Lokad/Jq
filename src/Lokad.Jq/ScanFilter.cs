using System.Collections.Generic;

namespace Lokad.Jq;

internal sealed class ScanFilter(IReadOnlyList<JqFilter> args) : JqFilter
{

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            await foreach (JqValue? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                foreach (JqValue? value in JqMatch.Scan(context, input, pattern, null))
                    yield return value;
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        await foreach (JqValue? flags in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JqValue? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                foreach (JqValue? value in JqMatch.Scan(context, input, pattern, flags))
                    yield return value;
    }
}
