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

// `halt` or `halt_error[(code)]`: immediate termination that user code can
// never observe or catch. The stderr text is rendered once at the throw
// site (strings raw, other values as JSON) so the executor only writes.
internal sealed class JqHaltException(int ExitCode, string? StderrText) : Exception("halt")
{
    internal int ExitCode { get; } = ExitCode;

    internal string? StderrText { get; } = StderrText;
}

// `break $label`: abandons the current computation up to the matching
// `label`, which then yields nothing further. Never an error value, so
// `try`, `?`, quotas, and cancellation never interact with it.
internal sealed class JqBreakException(string Label) : Exception("break")
{
    internal string Label { get; } = Label ?? throw new ArgumentNullException(nameof(Label));
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

// `label $name | BODY`: streams body outputs until a matching `break`
// abandons the rest. Prior outputs are kept; the break itself yields
// nothing. Non-matching breaks propagate to outer labels.
internal sealed class LabelFilter(string Name, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        using IEnumerator<JsonNode?> results = Body.Evaluate(input, context, environment).GetEnumerator();
        while (true)
        {
            bool moved;
            try
            {
                moved = results.MoveNext();
            }
            catch (JqBreakException broken) when (broken.Label == Name)
            {
                moved = false;
                break;
            }
            if (!moved)
                yield break;
            yield return results.Current;
        }
    }

    internal JqFilter LabelBody => Body;

    internal LabelFilter WithBody(JqFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new LabelFilter(Name, next);
    }
}

// `break $name`: throws to the lexically matching label. Yields nothing
// itself; unknown labels are rejected at parse time.
internal sealed class BreakFilter(string Name) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        throw new JqBreakException(Name);
    }
}
