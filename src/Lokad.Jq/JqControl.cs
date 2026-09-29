using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// An explicit `error` or `error(message)` failure. The payload, not the
// message, is what `catch` handlers observe; the message only renders when
// the error reaches the top level uncaught.
internal sealed class JqErrorException(JsonNode? payload, string message) : JqException(message)
{
    internal JsonNode? Payload { get; } = payload;
}

// `try BODY` or `try BODY catch HANDLER`: catchable evaluation failures
// discard partial body outputs and either vanish (no handler) or run the
// handler with the error payload as input. Quota, compile, cancellation,
// host, halt, break, and tail-call signals always propagate.
internal sealed class TryFilter(JqFilter Body, JqFilter? Handler) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        using IEnumerator<JsonNode?> results = Body.Evaluate(input, context, environment).GetEnumerator();
        while (true)
        {
            bool moved;
            JsonNode? failure = null;
            bool faulted = false;
            try
            {
                moved = results.MoveNext();
            }
            catch (Exception exception) when (JqErrors.IsCatchable(exception))
            {
                moved = false;
                if (Handler is not null)
                {
                    faulted = true;
                    failure = exception is JqErrorException user
                        ? context.Runtime.Clone(user.Payload)
                        : JsonValue.Create(exception.Message);
                }
            }
            if (faulted)
            {
                if (Handler is JqFilter handler)
                    foreach (JsonNode? value in handler.Evaluate(failure, context, environment))
                        yield return value;
                yield break;
            }
            if (!moved)
                yield break;
            yield return results.Current;
        }
    }

    internal JqFilter TryBody => Body;

    internal JqFilter? CatchHandler => Handler;

    internal TryFilter WithOperands(Func<JqFilter, JqFilter> rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        JqFilter? handler = Handler is null ? null : rewrite(Handler);
        return new TryFilter(rewrite(Body), handler);
    }
}
