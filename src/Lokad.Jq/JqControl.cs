using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// An explicit `error` or `error(message)` failure. The payload, not the
// message, is what `catch` handlers observe. The executor renders uncaught
// payloads; borrowing keeps their identity and existing storage ownership.
internal sealed class JqErrorException(JsonNode? payload) : JqException("error")
{
    internal JsonNode? Payload { get; } = payload;
}

// A sequence-record failure in `--seq` mode. The cursor has already
// advanced past the record separator, so the remaining sources stay readable
// and the executor reports `jq: ignoring parse error: ...` without aborting.
// Catchable by user code like other evaluation failures.
internal sealed class JqSeqResyncException(string message) : JqException(message);

// `halt` or `halt_error[(code)]`: immediate termination that user code can
// never observe or catch. The stderr text is rendered once at the throw
// site (strings raw, other values as JSON) so the executor only writes.
internal sealed class JqHaltException(int ExitCode, string? StderrText) : Exception("halt")
{
    internal int ExitCode { get; } = ExitCode;

    internal string? StderrText { get; } = StderrText;
}

// `break $label`: abandons the current computation up to the matching
// `label`, which then yields nothing further. Like the reference label error,
// an intervening try/optional scope can intercept it.
internal sealed class JqBreakException(JqEnvironment target) : JqException("break")
{
    internal JqEnvironment Target { get; } = target;
}

// `try BODY` or `try BODY catch HANDLER`: catchable evaluation failures
// keep earlier outputs and either vanish (no handler) or run the handler
// with the error payload as input. Quota, compile, cancellation,
// host, halt, and tail-call signals always propagate.
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
                    failure = JqErrors.Payload(exception, context.Budget);
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

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        using IEnumerator<JqValuePath> results = Body.EvaluatePaths(pair, context, environment).GetEnumerator();
        while (true)
        {
            bool moved;
            JqValuePath? failure = null;
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
                    JsonNode? payload = JqErrors.Payload(exception, context.Budget);
                    bool identical = ReferenceEquals(payload, pair.Value)
                        || ((payload is null || (payload is JsonValue scalar && !JqRuntime.TryGetString(scalar, out _)))
                            && PathIntact(context.Runtime, pair.Value, payload));
                    failure = pair.WithValue(payload, pair.Tracked && identical);
                }
            }
            if (faulted && failure is not null && Handler is JqFilter handler)
            {
                foreach (JqValuePath next in handler.EvaluatePaths(failure, context, environment))
                    yield return next;
                yield break;
            }
            if (!moved)
                yield break;
            yield return results.Current;
        }
    }


}

// `label $name | BODY`: streams body outputs until a matching `break`
// abandons the rest. Prior outputs are kept; the break itself yields
// nothing. Non-matching breaks propagate to outer labels.
internal sealed class LabelFilter(object Label, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        Run(context, environment, scope => Body.Evaluate(input, context, scope));

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        Run(context, environment, scope => Body.EvaluatePaths(pair, context, scope));

    private IEnumerable<T> Run<T>(JqContext context, JqEnvironment environment,
        Func<JqEnvironment, IEnumerable<T>> evaluate)
    {
        using var ownedStorage = context.Budget.TemporaryStorage is null ? new JqStorage(context.Budget.MemoryBudget) : null;
        JqStorage storage = context.Budget.TemporaryStorage ?? ownedStorage
            ?? throw new InvalidOperationException("Missing label storage owner.");
        context.Budget.CheckCancellation();
        storage.ReserveBytes(128, 1);
        try
        {
            JqEnvironment scope = environment.ExtendLabel(Label, context.NextLabel());
            using IEnumerator<T> results = evaluate(scope).GetEnumerator();
            while (true)
            {
                bool moved;
                try { moved = results.MoveNext(); }
                catch (JqBreakException broken) when (ReferenceEquals(broken.Target, scope)) { yield break; }
                catch (JqErrorException error) when (error.Payload is JsonObject { Count: 1 } payload
                    && payload.TryGetPropertyValue("__jq", out JsonNode? id)
                    && id?.GetValueKind() == JsonValueKind.Number && JqRuntime.Number(id) == scope.LabelId)
                { yield break; }
                if (!moved) yield break;
                yield return results.Current;
            }
        }
        finally { storage.DiscardBytes(128); }
    }


}

// `break $name`: throws to the lexically matching label. Yields nothing
// itself; unknown labels are rejected at parse time.
internal sealed class BreakFilter(object Label) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetLabel(Label, out JqEnvironment? target) || target is null)
            throw new InvalidOperationException("Missing lexical label activation.");
        throw new JqBreakException(target);
    }
}
