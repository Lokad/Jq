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

// A catchable sequence parse failure. The cursor retains its resynchronization
// state; the next pull can skip to RS. Outer iteration reports a warning.
internal sealed class JqSeqResyncException(string message) : JqException(message);

// Stop producing after a host append closes or fails. This unwind signal is
// separate from an evaluation failure, so owned cleanup faults remain observable.
internal sealed class JqOutputClosedException(int exitCode) : Exception("output closed")
{
    internal int ExitCode { get; } = exitCode;
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
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var results = Body.Evaluate(input, context, environment).GetAsyncEnumerator();
        await using var resultsLifetime = results.ConfigureAwait(false);
        while (true)
        {
            bool moved;
            JsonNode? failure = null;
            bool faulted = false;
            try
            {
                moved = await results.MoveNextAsync().ConfigureAwait(false);
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
                    await foreach (JsonNode? value in handler.Evaluate(failure, context, environment).ConfigureAwait(false))
                        yield return value;
                yield break;
            }
            if (!moved)
                yield break;
            yield return results.Current;
        }
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        var results = Body.EvaluatePaths(pair, context, environment).GetAsyncEnumerator();
        await using var resultsLifetime = results.ConfigureAwait(false);
        while (true)
        {
            bool moved;
            JqValuePath? failure = null;
            bool faulted = false;
            try
            {
                moved = await results.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (JqErrors.IsCatchable(exception))
            {
                moved = false;
                if (Handler is not null)
                {
                    faulted = true;
                    JsonNode? payload = JqErrors.Payload(exception, context.Budget);
                    failure = TrackResult(pair, payload);
                }
            }
            if (faulted && failure is not null && Handler is JqFilter handler)
            {
                await foreach (JqValuePath next in handler.EvaluatePaths(failure, context, environment).ConfigureAwait(false))
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
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        Run(context, environment, scope => Body.Evaluate(input, context, scope));

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        Run(context, environment, scope => Body.EvaluatePaths(pair, context, scope));

    private async IAsyncEnumerable<T> Run<T>(JqContext context, JqEnvironment environment,
        Func<JqEnvironment, IAsyncEnumerable<T>> evaluate)
    {
        using var ownedStorage = context.Budget.TemporaryStorage is null ? new JqStorage(context.Budget.MemoryBudget) : null;
        JqStorage storage = context.Budget.TemporaryStorage ?? ownedStorage
            ?? throw new InvalidOperationException("Missing label storage owner.");
        context.Budget.CheckCancellation();
        storage.ReserveBytes(128, 1);
        try
        {
            JqEnvironment scope = environment.ExtendLabel(Label, context.NextLabel());
            var results = evaluate(scope).GetAsyncEnumerator();
            await using var resultsLifetime = results.ConfigureAwait(false);
            while (true)
            {
                bool moved;
                try { moved = await results.MoveNextAsync().ConfigureAwait(false); }
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
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetLabel(Label, out JqEnvironment? target) || target is null)
            throw new InvalidOperationException("Missing lexical label activation.");
        throw new JqBreakException(target);
    }
}
