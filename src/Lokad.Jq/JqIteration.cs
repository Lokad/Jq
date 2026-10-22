using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// `reduce SOURCE as PATTERNS (INIT; UPDATE)`: folds source items into a
// single answer per initializer. Initial values stream outermost; per item,
// the update runs against the running state in the matched environment
// (first-match-wins with catchable retries, mirroring bindings). Like the
// reference cell overwrite (compile.c gen_reduce, gojq compileReduce), the
// last update output wins and earlier outputs are dropped; an update with no
// outputs empties the cell (reference LOADVN slot) and the run continues.
// Each initializer yields its final state once, even when no update
// ever produced a value.
internal sealed class ReduceFilter(JqFilter Source, IReadOnlyList<BindingPattern> Alternatives, JqFilter Init, JqFilter Update) : JqFilter
{
    private readonly HashSet<string> _allNames = AsFilter.CollectAll(Alternatives);

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? initial in Init.Evaluate(input, context, environment))
        {
            JsonNode? state = initial;
            JqStorage? trackedStorage = null;
            long retainedUpdate = 0;
            foreach (JsonNode? item in Source.Evaluate(input, context, environment))
            {
                JqStorage? storage = context.Budget.TemporaryStorage;
                if (!ReferenceEquals(storage, trackedStorage)) retainedUpdate = 0;
                trackedStorage = storage;
                long before = storage?.UsedBytes ?? 0;
                bool produced = false;
                JsonNode? last = null;
                foreach (var (_, updated) in AsFilter.DriveAlternatives(Alternatives, _allNames, item, environment, context, scope => Update.Evaluate(state, context, scope)))
                {
                    context.Budget.CheckCancellation();
                    produced = true;
                    last = updated;
                }

                // Like the reference LOADVN slot, an update with no outputs
                // empties the cell and the run continues with the next item.
                JsonNode? next = produced ? last : null;
                if (storage is not null && IsStandaloneScalar(state) && IsStandaloneScalar(next))
                {
                    long currentUpdate = storage.UsedBytes - before;
                    // Scalars cannot retain another value graph. A string may
                    // share its old backing, so preserve that admission instead
                    // of refunding it. Initial/source/collected values remain
                    // outside this update's demand and keep their parent charge.
                    bool shared = ReferenceEquals(state, next)
                        || TryGetString(state, out var oldText) && TryGetString(next, out var newText)
                            && ReferenceEquals(oldText, newText);
                    state = next;
                    if (shared) storage.DiscardBytes(currentUpdate);
                    else
                    {
                        storage.DiscardBytes(retainedUpdate);
                        retainedUpdate = currentUpdate;
                    }
                }
                else
                {
                    // Containers and opaque scalar backing keep conservative
                    // ownership. Do not traverse aliases to reclaim history.
                    retainedUpdate = 0;
                    state = next;
                }
            }

            yield return state;
        }

        static bool IsStandaloneScalar(JsonNode? value) => value is null
            || value is JsonValue scalar && !scalar.TryGetValue<JsonElement>(out _)
                && (scalar.TryGetValue<string>(out _) || scalar.TryGetValue<long>(out _)
                    || scalar.TryGetValue<int>(out _) || scalar.TryGetValue<double>(out _)
                    || scalar.TryGetValue<bool>(out _));
    }
}

// `foreach SOURCE as PATTERNS (INIT; UPDATE[; EXTRACT])`: like reduce, but
// yields every intermediate state (or the extraction run against each
// updated state in the matched environment). Initial values never yield
// without items; each update output is emitted while only the last threads
// forward as state. An update with no outputs empties the cell like the
// reference LOADVN slot, skips extraction, and continues with the next item.
internal sealed class ForeachFilter(JqFilter Source, IReadOnlyList<BindingPattern> Alternatives, JqFilter Init, JqFilter Update, JqFilter? Extract) : JqFilter
{
    private readonly HashSet<string> _allNames = AsFilter.CollectAll(Alternatives);

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? initial in Init.Evaluate(input, context, environment))
        {
            JsonNode? state = initial;
            foreach (JsonNode? item in Source.Evaluate(input, context, environment))
            {
                bool produced = false;
                JsonNode? last = null;
                foreach (var (scope, updated) in AsFilter.DriveAlternatives(Alternatives, _allNames, item, environment, context, scope => Update.Evaluate(state, context, scope)))
                {
                    context.Budget.CheckCancellation();
                    produced = true;
                    last = updated;
                    if (Extract is null)
                        yield return updated;
                    else
                        foreach (JsonNode? extracted in Extract.Evaluate(updated, context, scope))
                            yield return extracted;
                }

                // Like the reference LOADVN slot, an update with no outputs
                // empties the cell, skips extraction, and continues.
                state = produced ? last : null;
            }
        }
    }
}

// `limit($n; EXPR)`: at most ceil(n) outputs. Zero never evaluates the
// argument; negative counts raise a catchable error.
internal sealed class LimitFilter(JqFilter Count, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? count in Count.Evaluate(input, context, environment))
        {
            // Null and booleans rank below numbers, so the reference order branches reject them like negatives.
            if (count is null || (count is JsonValue countBool && countBool.TryGetValue<bool>(out _)))
                throw new JqException("limit doesn't support negative count");
            double total = Number(count);
            if (total == 0)
                continue;
            if (!(total > 0))
                throw new JqException("limit doesn't support negative count");
            long pulled = 0;
            foreach (JsonNode? value in Body.Evaluate(input, context, environment))
            {
                yield return value;
                if (++pulled >= total)
                    break;
            }
        }
    }
}

// `first(EXPR)`: the first output only, pulled lazily.
internal sealed class FirstFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? value in Body.Evaluate(input, context, environment))
        {
            yield return value;
            yield break;
        }
    }
}

// `last(EXPR)`: the last output only, empty when the argument yields nothing.
internal sealed class LastFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        bool empty = true;
        JsonNode? last = null;
        foreach (JsonNode? value in Body.Evaluate(input, context, environment))
        {
            empty = false;
            last = value;
        }
        if (!empty)
            yield return last;
    }
}

// `nth($n; EXPR)`: drops n outputs, then yields the next once.
internal sealed class NthFilter(JqFilter Index, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? index in Index.Evaluate(input, context, environment))
        {
            // Null and booleans rank below numbers, so the reference order branches reject them like negatives.
            if (index is null || (index is JsonValue indexBool && indexBool.TryGetValue<bool>(out _)))
                throw new JqException("nth doesn't support negative indices");
            double position = Number(index);
            if (position < 0)
                throw new JqException("nth doesn't support negative indices");
            // The desugared skip surfaces its own diagnostic for NaN like the
            // reference else branch (NaN is neither positive nor zero).
            if (double.IsNaN(position))
                throw new JqException("skip doesn't support negative count");
            long skip = (long)position;
            using IEnumerator<JsonNode?> results = Body.Evaluate(input, context, environment).GetEnumerator();
            bool exhausted = false;
            for (long taken = 0; taken < skip; taken++)
            {
                if (!results.MoveNext())
                {
                    exhausted = true;
                    break;
                }
            }
            if (!exhausted && results.MoveNext())
                yield return results.Current;
        }
    }
}

// `isempty(EXPR)`: true when the argument yields nothing. Pulls at most one
// output; argument errors propagate instead of reading as empty.
internal sealed class IsemptyFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        using IEnumerator<JsonNode?> results = Body.Evaluate(input, context, environment).GetEnumerator();
        yield return JsonValue.Create(!results.MoveNext());
    }
}

// Loops share a lazy depth-first traversal; each condition output is a branch.
internal sealed class WhileFilter(JqFilter Condition, JqFilter Update) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        JqLoop.Evaluate(input, context, environment, Condition, Update, JqLoop.Kind.While);
}

internal sealed class UntilFilter(JqFilter Condition, JqFilter Next) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        JqLoop.Evaluate(input, context, environment, Condition, Next, JqLoop.Kind.Until);
}

// `repeat(filter)`: re-evaluates the argument against the original input
// forever. Cancellation stops iteration; body construction uses shared storage.
internal sealed class RepeatFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        while (true)
        {
            context.Budget.CheckCancellation();
            foreach (JsonNode? value in Body.Evaluate(input, context, environment))
                yield return value;
        }
    }
}

// `recurse(filter[; condition])`: condition outputs filter expanded states.
internal sealed class RecurseFilter(JqFilter Body, JqFilter? Condition) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        JqLoop.Evaluate(input, context, environment, Condition, Body, JqLoop.Kind.Recurse);
}

// Pending siblings stay lazy. Only genuinely branching continuations are retained;
// proven single-valued tails run flat without evaluating the next sibling early.
internal static class JqLoop
{
    internal enum Kind { While, Until, Recurse }
    private readonly record struct Step(JsonNode? Value, bool Descend, bool Tail);

    internal static IEnumerable<JsonNode?> Evaluate(JsonNode? input, JqContext context,
        JqEnvironment environment, JqFilter? condition, JqFilter update, Kind kind)
    {
        bool singleUpdate = update.IsSingleValued(environment, 0);
        bool singleCondition = condition is null || condition.IsSingleValued(environment, 0);
        using var memory = new JqMemoryReservation(context.Budget.MemoryBudget);
        var pending = new List<IEnumerator<Step>>();
        IEnumerator<Step>? current = Steps(input).GetEnumerator();
        try
        {
            while (Advance(out JsonNode? value)) yield return value;
        }
        finally { DisposeAll(null); }

        bool Advance(out JsonNode? value)
        {
            try
            {
                while (current is not null)
                {
                    context.Budget.CheckCancellation();
                    if (!current.MoveNext())
                    {
                        IEnumerator<Step> completed = current;
                        current = null;
                        completed.Dispose();
                        if (pending.Count == 0) break;
                        current = pending[^1];
                        pending.RemoveAt(pending.Count - 1);
                        continue;
                    }
                    Step step = current.Current;
                    if (!step.Descend)
                    {
                        value = step.Value;
                        return true;
                    }
                    if (step.Tail)
                    {
                        IEnumerator<Step> completed = current;
                        current = null;
                        completed.Dispose();
                    }
                    else
                    {
                        if (pending.Count == pending.Capacity)
                        {
                            int capacity = checked(Math.Max(4, pending.Capacity * 2));
                            // Slots include suspended iterators and their filter cursors.
                            long previous = memory.LastAdmittedBytes;
                            memory.Resize(checked(previous + 512L * capacity));
                            pending.Capacity = capacity;
                            if (previous != 0) memory.Resize(512L * capacity);
                        }
                        pending.Add(current);
                        current = null;
                    }
                    current = Steps(step.Value).GetEnumerator();
                }
                value = null;
                return false;
            }
            catch (Exception exception)
            {
                DisposeAll(exception);
                throw;
            }
        }

        void DisposeAll(Exception? primary)
        {
            Exception? failure = primary;
            Dispose(current);
            current = null;
            for (int index = pending.Count - 1; index >= 0; index--) Dispose(pending[index]);
            pending.Clear();
            if (failure is not null && !ReferenceEquals(failure, primary))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            void Dispose(IEnumerator<Step>? iterator)
            {
                try { iterator?.Dispose(); }
                catch (Exception exception)
                {
                    // Retain an earlier terminal failure, but never hide terminal
                    // refusal/cancellation behind a catchable language error.
                    if (failure is null || JqErrors.IsCatchable(failure) && !JqErrors.IsCatchable(exception))
                        failure = exception;
                }
            }
        }

        IEnumerable<Step> Steps(JsonNode? state)
        {
            if (kind == Kind.Recurse)
            {
                yield return new Step(state, false, false);
                foreach (JsonNode? expanded in update.Evaluate(state, context, environment))
                {
                    if (condition is null)
                        yield return new Step(expanded, true, singleUpdate);
                    else
                        foreach (JsonNode? probe in condition.Evaluate(expanded, context, environment))
                            if (Truthy(probe))
                                yield return new Step(expanded, true, singleUpdate && singleCondition);
                }
                yield break;
            }
            if (condition is null) throw new InvalidOperationException("A loop condition is required.");
            foreach (JsonNode? probe in condition.Evaluate(state, context, environment))
            {
                bool truthy = Truthy(probe);
                if (kind == Kind.Until && truthy)
                    yield return new Step(state, false, false);
                else if (kind == Kind.While && !truthy)
                    continue;
                else
                {
                    if (kind == Kind.While) yield return new Step(state, false, false);
                    foreach (JsonNode? updated in update.Evaluate(state, context, environment))
                        yield return new Step(updated, true, singleCondition && singleUpdate);
                }
            }
        }
    }
}

// `walk(filter)`: bottom-up traversal. Array levels rebuild by collecting
// every child walk output (reference map), object levels keep the first
// output per value and drop empties (reference map_values modify
// first-only, like update assignment), and the filter then runs against
// each rebuilt node, streaming every output upward.
internal sealed class WalkFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        using var memory = new JqMemoryReservation(context.Budget.MemoryBudget);
        var pending = new List<WalkFrame>();
        JsonNode? current = input;
        string? key = null;
        bool visit = true;
        try
        {
            while (true)
            {
                context.Budget.CheckCancellation();
                if (visit && current is JsonArray or JsonObject)
                {
                    if (pending.Count == pending.Capacity)
                    {
                        int capacity = checked(Math.Max(4, pending.Capacity * 2));
                        long previous = memory.LastAdmittedBytes;
                        memory.Resize(checked(previous + 128L * capacity));
                        pending.Capacity = capacity;
                        if (previous != 0) memory.Resize(128L * capacity);
                    }
                    context.Budget.ReserveBytes(64);
                    var frame = new WalkFrame(current, key);
                    pending.Add(frame);
                    if (frame.TryNext(out current, out key)) continue;
                    current = frame.Result;
                    key = frame.Key;
                    pending.RemoveAt(pending.Count - 1);
                    frame.Dispose();
                }
                if (pending.Count == 0)
                {
                    foreach (JsonNode? value in Body.Evaluate(current, context, environment)) yield return value;
                    yield break;
                }
                WalkFrame parent = pending[^1];
                if (parent.Result is JsonArray array)
                {
                    foreach (JsonNode? value in Body.Evaluate(current, context, environment))
                    {
                        context.Budget.ReserveArrayItem(value);
                        array.Add(context.Runtime.Detach(value));
                    }
                }
                else if (parent.Result is JsonObject obj && key is not null)
                {
                    using var outputs = Body.Evaluate(current, context, environment).GetEnumerator();
                    if (outputs.MoveNext())
                    {
                        context.Budget.ReserveObjectItem(outputs.Current);
                        obj.Add(key, context.Runtime.Detach(outputs.Current));
                    }
                }
                if (parent.TryNext(out current, out key))
                {
                    visit = true;
                    continue;
                }
                current = parent.Result;
                key = parent.Key;
                pending.RemoveAt(pending.Count - 1);
                parent.Dispose();
                visit = false;
            }
        }
        finally
        {
            for (int index = pending.Count - 1; index >= 0; index--) pending[index].Dispose();
        }
    }

    // Only open container cursors survive descent. Scalar children need no frame.
    private sealed class WalkFrame : IDisposable
    {
        private readonly JsonNode _source;
        private readonly IEnumerator<KeyValuePair<string, JsonNode?>>? _properties;
        private int _index;
        internal JsonNode Result { get; }
        internal string? Key { get; }

        internal WalkFrame(JsonNode source, string? key)
        {
            _source = source;
            Key = key;
            if (source is JsonObject obj)
            {
                Result = new JsonObject();
                _properties = obj.GetEnumerator();
            }
            else Result = new JsonArray();
        }

        internal bool TryNext(out JsonNode? child, out string? key)
        {
            if (_source is JsonArray array && _index < array.Count)
            {
                child = array[_index++];
                key = null;
                return true;
            }
            if (_properties is not null && _properties.MoveNext())
            {
                child = _properties.Current.Value;
                key = _properties.Current.Key;
                return true;
            }
            child = null;
            key = null;
            return false;
        }

        public void Dispose() => _properties?.Dispose();
    }
}

// `paths` and `paths(filter)`: non-empty descent paths, optionally keeping
// only nodes for each truthy filter output, mirroring select.
internal sealed class PathsFilter(JqFilter? Condition) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        using var cursor = new JqTreeCursor(context.Budget);
        JsonNode? current = input;
        while (true)
        {
            if (current is JsonArray { Count: > 0 } or JsonObject { Count: > 0 }) cursor.Push(current);
            while (cursor.Count != 0 && !cursor.TryNext(out current)) cursor.Pop();
            if (cursor.Count == 0) yield break;
            if (Condition is null)
            {
                yield return cursor.CopyJsonPath();
                continue;
            }
            // Like select, every condition output counts: each truthy probe keeps
            // the path, matching path(recurse|select(node_filter)) multiplicity.
            foreach (JsonNode? probe in Condition.Evaluate(current, context, environment))
            {
                if (Truthy(probe))
                    yield return cursor.CopyJsonPath();
            }
        }
    }
}

// `skip($n; EXPR)`: drops n outputs, then streams the rest.
internal sealed class SkipFilter(JqFilter Count, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? count in Count.Evaluate(input, context, environment))
        {
            // Null and booleans rank below numbers, so the reference order branches reject them like negatives.
            if (count is null || (count is JsonValue skipBool && skipBool.TryGetValue<bool>(out _)))
                throw new JqException("skip doesn't support negative count");
            double total = Number(count);
            // NaN falls through to the reference else branch like negatives.
            if (total < 0 || double.IsNaN(total))
                throw new JqException("skip doesn't support negative count");
            long skip = (long)total;
            using IEnumerator<JsonNode?> results = Body.Evaluate(input, context, environment).GetEnumerator();
            for (long dropped = 0; dropped < skip; dropped++)
            {
                if (!results.MoveNext())
                    break;
            }
            while (results.MoveNext())
                yield return results.Current;
        }
    }
}
