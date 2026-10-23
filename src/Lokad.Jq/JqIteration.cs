using System.Runtime.CompilerServices;
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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? initial in Init.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            JsonNode? state = initial;
            JqStorage? trackedStorage = null;
            long retainedUpdate = 0;
            await foreach (JsonNode? item in Source.Evaluate(input, context, environment).ConfigureAwait(false))
            {
                JqStorage? storage = context.Budget.TemporaryStorage;
                if (!ReferenceEquals(storage, trackedStorage)) retainedUpdate = 0;
                trackedStorage = storage;
                long before = storage?.UsedBytes ?? 0;
                bool produced = false;
                JsonNode? last = null;
                await foreach (var (_, updated) in AsFilter.DriveAlternatives(Alternatives, _allNames, item, environment, context, scope => Update.Evaluate(state, context, scope)).ConfigureAwait(false))
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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? initial in Init.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            JsonNode? state = initial;
            await foreach (JsonNode? item in Source.Evaluate(input, context, environment).ConfigureAwait(false))
            {
                bool produced = false;
                JsonNode? last = null;
                await foreach (var (scope, updated) in AsFilter.DriveAlternatives(Alternatives, _allNames, item, environment, context, scope => Update.Evaluate(state, context, scope)).ConfigureAwait(false))
                {
                    context.Budget.CheckCancellation();
                    produced = true;
                    last = updated;
                    if (Extract is null)
                        yield return updated;
                    else
                        await foreach (JsonNode? extracted in Extract.Evaluate(updated, context, scope).ConfigureAwait(false))
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
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        Select(input, context, environment, () => Body.Evaluate(input, context, environment));

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        Select(pair.Value, context, environment, () => Body.EvaluatePaths(pair, context, environment));

    private async IAsyncEnumerable<T> Select<T>(JsonNode? input, JqContext context, JqEnvironment environment, Func<IAsyncEnumerable<T>> outputs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? count in Count.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            bool numeric = TypeName(count) == "number";
            double remaining = numeric ? Number(count) : 0;
            if (numeric && remaining == 0) continue;
            if (numeric ? !(remaining > 0) : context.Runtime.Compare(count, JsonValue.Create(0)) <= 0)
                throw new JqException("limit doesn't support negative count");
            await foreach (T value in outputs().ConfigureAwait(false))
            {
                // The reference subtracts only after the generator yields.
                if (!numeric) _ = context.Runtime.Subtract(count, JsonValue.Create(1));
                remaining -= 1;
                yield return value;
                if (remaining <= 0) break;
            }
        }
    }
}

// `first(EXPR)`: the first output only, pulled lazily.
internal sealed class FirstFilter(JqFilter Body) : JqFilter
{
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        Body.Evaluate(input, context, environment).Take(1);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        Body.EvaluatePaths(pair, context, environment).Take(1);
}

// `last(EXPR)`: the last output only, empty when the argument yields nothing.
internal sealed class LastFilter(JqFilter Body) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        bool empty = true;
        JsonNode? last = null;
        await foreach (JsonNode? value in Body.Evaluate(input, context, environment).ConfigureAwait(false))
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
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        Select(input, context, environment, () => Body.Evaluate(input, context, environment));

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        Select(pair.Value, context, environment, () => Body.EvaluatePaths(pair, context, environment));

    private async IAsyncEnumerable<T> Select<T>(JsonNode? input, JqContext context, JqEnvironment environment, Func<IAsyncEnumerable<T>> outputs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? index in Index.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            bool numeric = TypeName(index) == "number";
            double remaining = numeric ? Number(index) : 0;
            if (numeric ? !(remaining >= 0) : context.Runtime.Compare(index, JsonValue.Create(0)) < 0)
                throw new JqException("nth doesn't support negative indices");
            await foreach (T value in outputs().ConfigureAwait(false))
            {
                if (!numeric) _ = context.Runtime.Subtract(index, JsonValue.Create(1));
                remaining -= 1;
                if (remaining >= 0) continue;
                yield return value;
                break;
            }
        }
    }
}

// `isempty(EXPR)`: true when the argument yields nothing. Pulls at most one
// output; argument errors propagate instead of reading as empty.
internal sealed class IsemptyFilter(JqFilter Body) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var results = Body.Evaluate(input, context, environment).GetAsyncEnumerator();
        await using var resultsLifetime = results.ConfigureAwait(false);
        yield return JsonValue.Create(!await results.MoveNextAsync().ConfigureAwait(false));
    }
}

// Loops share a lazy depth-first traversal; each condition output is a branch.
internal sealed class WhileFilter(JqFilter Condition, JqFilter Update) : JqFilter
{
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        JqLoop.Evaluate(input, context, environment, Condition, Update, JqLoop.Kind.While);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        JqLoop.EvaluatePaths(pair, context, environment, Condition, Update, JqLoop.Kind.While);
}

internal sealed class UntilFilter(JqFilter Condition, JqFilter Next) : JqFilter
{
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        JqLoop.Evaluate(input, context, environment, Condition, Next, JqLoop.Kind.Until);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        JqLoop.EvaluatePaths(pair, context, environment, Condition, Next, JqLoop.Kind.Until);
}

// `repeat(filter)`: re-evaluates the argument against the original input
// forever. Cancellation stops iteration; body construction uses shared storage.
internal sealed class RepeatFilter(JqFilter Body) : JqFilter
{
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        Repeat(Body.Evaluate(input, context, environment), context.Budget);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        Repeat(Body.EvaluatePaths(pair, context, environment), context.Budget);

    private static async IAsyncEnumerable<T> Repeat<T>(IAsyncEnumerable<T> outputs, JqBudget budget)
    {
        while (true)
        {
            budget.CheckCancellation();
            await foreach (T value in outputs.ConfigureAwait(false)) yield return value;
        }
    }
}

// `recurse(filter[; condition])`: condition outputs filter expanded states.
internal sealed class RecurseFilter(JqFilter Body, JqFilter? Condition) : JqFilter
{
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        JqLoop.Evaluate(input, context, environment, Condition, Body, JqLoop.Kind.Recurse);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        JqLoop.EvaluatePaths(pair, context, environment, Condition, Body, JqLoop.Kind.Recurse);
}

// Pending siblings stay lazy. Only genuinely branching continuations are retained;
// proven single-valued tails run flat without evaluating the next sibling early.
internal static class JqLoop
{
    internal enum Kind { While, Until, Recurse }
    private readonly record struct Step<T>(T Value, bool Descend, bool Tail);

    internal static IAsyncEnumerable<JsonNode?> Evaluate(JsonNode? input, JqContext context,
        JqEnvironment environment, JqFilter? condition, JqFilter update, Kind kind) =>
        Run(input, context, environment, condition, update, kind,
            state => update.Evaluate(state, context, environment), static state => state);

    internal static IAsyncEnumerable<JqValuePath> EvaluatePaths(JqValuePath pair, JqContext context,
        JqEnvironment environment, JqFilter? condition, JqFilter update, Kind kind) =>
        Run(pair, context, environment, condition, update, kind,
            state => update.EvaluatePaths(state, context, environment), static state => state.Value);

    private static async IAsyncEnumerable<T> Run<T>(T input, JqContext context, JqEnvironment environment,
        JqFilter? condition, JqFilter update, Kind kind, Func<T, IAsyncEnumerable<T>> next, Func<T, JsonNode?> valueOf)
    {
        bool singleUpdate = update.IsSingleValued(environment, 0);
        bool singleCondition = condition is null || condition.IsSingleValued(environment, 0);
        using var memory = new JqMemoryReservation(context.Budget.MemoryBudget);
        var pending = new List<IAsyncEnumerator<Step<T>>>();
        IAsyncEnumerator<Step<T>>? current = Steps(input).GetAsyncEnumerator();
        try
        {
            while (await Advance().ConfigureAwait(false) is { } output) yield return output.Value;
        }
        finally { await DisposeAll(null).ConfigureAwait(false); }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        async ValueTask<Step<T>?> Advance()
        {
            try
            {
                while (current is not null)
                {
                    context.Budget.CheckCancellation();
                    if (!await current.MoveNextAsync().ConfigureAwait(false))
                    {
                        IAsyncEnumerator<Step<T>> completed = current;
                        current = null;
                        await completed.DisposeAsync().ConfigureAwait(false);
                        if (pending.Count == 0) break;
                        current = pending[^1];
                        pending.RemoveAt(pending.Count - 1);
                        continue;
                    }
                    Step<T> step = current.Current;
                    if (!step.Descend)
                    {
                        return step;
                    }
                    if (step.Tail)
                    {
                        IAsyncEnumerator<Step<T>> completed = current;
                        current = null;
                        await completed.DisposeAsync().ConfigureAwait(false);
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
                    current = Steps(step.Value).GetAsyncEnumerator();
                }
                return null;
            }
            catch (Exception exception)
            {
                await DisposeAll(exception).ConfigureAwait(false);
                throw;
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        async ValueTask DisposeAll(Exception? primary)
        {
            Exception? failure = primary;
            await Dispose(current).ConfigureAwait(false);
            current = null;
            for (int index = pending.Count - 1; index >= 0; index--) await Dispose(pending[index]).ConfigureAwait(false);
            pending.Clear();
            if (failure is not null && !ReferenceEquals(failure, primary))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
            async ValueTask Dispose(IAsyncEnumerator<Step<T>>? iterator)
            {
                try { if (iterator is not null) await iterator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    // Retain an earlier terminal failure, but never hide terminal
                    // refusal/cancellation behind a catchable language error.
                    if (failure is null || JqErrors.IsCatchable(failure) && !JqErrors.IsCatchable(exception))
                        failure = exception;
                }
            }
        }

        async IAsyncEnumerable<Step<T>> Steps(T state)
        {
            if (kind == Kind.Recurse)
            {
                yield return new Step<T>(state, false, false);
                await foreach (T expanded in next(state).ConfigureAwait(false))
                {
                    if (condition is null)
                        yield return new Step<T>(expanded, true, singleUpdate);
                    else
                        await foreach (JsonNode? probe in condition.Evaluate(valueOf(expanded), context, environment).ConfigureAwait(false))
                            if (Truthy(probe))
                                yield return new Step<T>(expanded, true, singleUpdate && singleCondition);
                }
                yield break;
            }
            if (condition is null) throw new InvalidOperationException("A loop condition is required.");
            await foreach (JsonNode? probe in condition.Evaluate(valueOf(state), context, environment).ConfigureAwait(false))
            {
                bool truthy = Truthy(probe);
                if (kind == Kind.Until && truthy)
                    yield return new Step<T>(state, false, false);
                else if (kind == Kind.While && !truthy)
                    continue;
                else
                {
                    if (kind == Kind.While) yield return new Step<T>(state, false, false);
                    await foreach (T updated in next(state).ConfigureAwait(false))
                        yield return new Step<T>(updated, true, singleCondition && singleUpdate);
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
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
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
                    await foreach (JsonNode? value in Body.Evaluate(current, context, environment).ConfigureAwait(false)) yield return value;
                    yield break;
                }
                WalkFrame parent = pending[^1];
                if (parent.Result is JsonArray array)
                {
                    await foreach (JsonNode? value in Body.Evaluate(current, context, environment).ConfigureAwait(false))
                    {
                        context.Budget.ReserveArrayItem(value);
                        array.Add(context.Runtime.Detach(value));
                    }
                }
                else if (parent.Result is JsonObject obj && key is not null)
                {
                    var outputs = Body.Evaluate(current, context, environment).GetAsyncEnumerator();
                    await using var outputsLifetime = outputs.ConfigureAwait(false);
                    if (await outputs.MoveNextAsync().ConfigureAwait(false))
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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
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
            await foreach (JsonNode? probe in Condition.Evaluate(current, context, environment).ConfigureAwait(false))
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
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        Select(input, context, environment, () => Body.Evaluate(input, context, environment));

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        Select(pair.Value, context, environment, () => Body.EvaluatePaths(pair, context, environment));

    private async IAsyncEnumerable<T> Select<T>(JsonNode? input, JqContext context, JqEnvironment environment, Func<IAsyncEnumerable<T>> outputs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? count in Count.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            bool numeric = TypeName(count) == "number";
            double remaining = numeric ? Number(count) : 0;
            if (numeric ? !(remaining >= 0) : context.Runtime.Compare(count, JsonValue.Create(0)) <= 0)
                throw new JqException("skip doesn't support negative count");
            await foreach (T value in outputs().ConfigureAwait(false))
            {
                if (!numeric) _ = context.Runtime.Subtract(count, JsonValue.Create(1));
                remaining -= 1;
                if (remaining < 0) yield return value;
            }
        }
    }
}
