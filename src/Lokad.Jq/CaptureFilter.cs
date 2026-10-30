using System.Collections.Generic;

namespace Lokad.Jq;

internal sealed class CaptureFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        if (args.Count == 1)
        {
            await foreach (JqValue? value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
            {
                JqMatch.SplitArgument(context, value, out JqValue? pattern, out JqValue? modifiers);
                foreach (JqValue match in JqMatch.Search(context, input, pattern, modifiers))
                    yield return JqMatch.ToNamedObject(context, (JqObject)match);
            }
            yield break;
        }
        // jq evaluates the flags stream before the pattern stream.
        await foreach (JqValue? flags in args[1].Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JqValue? pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                foreach (JqValue match in JqMatch.Search(context, input, pattern, flags))
                    yield return JqMatch.ToNamedObject(context, (JqObject)match);
    }
}
