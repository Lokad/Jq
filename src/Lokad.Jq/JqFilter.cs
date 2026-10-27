using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Jq.Helpers;
using static Lokad.Jq.JqRuntime;
using static Lokad.Jq.JqPaths;

namespace Lokad.Jq;

internal abstract class JqFilter
{
    public virtual async IAsyncEnumerable<JsonNode?> Evaluate(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        var values = EvaluateCore(input, context, environment).GetAsyncEnumerator();
        await using var lifetime = values.ConfigureAwait(false);
        while (await context.Budget.MoveNextAsync(values).ConfigureAwait(false))
        {
            context.Budget.CheckCancellation();
            yield return values.Current;
        }
    }

    /// <summary>Evaluates within the caller's shared budget; charge before growing intermediate values.</summary>
    protected abstract IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment);

    internal async IAsyncEnumerable<JqValuePath> EvaluatePaths(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var paths = EvaluatePathsCore(pair, context, environment).GetAsyncEnumerator();
        await using var lifetime = paths.ConfigureAwait(false);
        while (await context.Budget.MoveNextAsync(paths).ConfigureAwait(false))
        {
            context.Budget.CheckCancellation();
            yield return paths.Current;
        }
    }

    // Proven cardinality lets iterative traversals discard exhausted tail frames
    // without probing ahead into deferred errors or side effects.
    internal virtual bool IsSingleValued(JqEnvironment environment, int depth) => false;

    internal static JqValuePath TrackResult(JqValuePath pair, JsonNode? value)
    {
        JqValuePath? origin = pair.Tracked ? pair : pair.Origin;
        bool intact = origin is not null && PathIntact(origin.Value, value);
        return intact && origin is not null ? origin.WithValue(value, true) : pair.WithValue(value, false);
    }

    protected virtual async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        await foreach (JsonNode? value in Evaluate(pair.Value, context, environment).ConfigureAwait(false))
            yield return TrackResult(pair, value);
    }

    // Heap values retain their identity by reference. Immediate numbers keep
    // their representation, including signed zero and NaN bits.
    internal static bool PathIntact(JsonNode? expected, JsonNode? actual)
    {
        if (ReferenceEquals(expected, actual)) return true;
        if (expected is JsonValue a && actual is JsonValue b
            && a.TryGetValue<bool>(out bool x) && b.TryGetValue<bool>(out bool y)) return x == y;
        return TypeName(expected) == "number" && TypeName(actual) == "number"
            && BitConverter.DoubleToInt64Bits(Number(expected)) == BitConverter.DoubleToInt64Bits(Number(actual));
    }
}

internal abstract class SingleValueFilter : JqFilter
{
    internal sealed override bool IsSingleValued(JqEnvironment environment, int depth) => true;

    // Pure leaves need no asynchronous state machine. Keep their single cursor
    // small while retaining lazy evaluation and the recursion/cancellation guard.
    public sealed override IAsyncEnumerable<JsonNode?> Evaluate(JsonNode? input, JqContext context, JqEnvironment environment) =>
        new ValueCursor(this, input, context, environment);

    private sealed class ValueCursor(SingleValueFilter filter, JsonNode? input, JqContext context, JqEnvironment environment)
        : IAsyncEnumerable<JsonNode?>, IAsyncEnumerator<JsonNode?>
    {
        private int _enumerated;
        private bool _started;
        public JsonNode? Current { get; private set; }

        public IAsyncEnumerator<JsonNode?> GetAsyncEnumerator(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) == 0) return this;
            return new ValueCursor(filter, input, context, environment) { _enumerated = 1 };
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_started) { Current = null; return ValueTask.FromResult(false); }
            _started = true;
            context.Budget.EnterRecursion();
            try
            {
                Current = filter.EvaluateValue(input, context, environment);
                context.Budget.CheckCancellation();
                return ValueTask.FromResult(true);
            }
            finally { context.Budget.LeaveRecursion(); }
        }

        public ValueTask DisposeAsync()
        {
            _started = true;
            Current = null;
            return default;
        }
    }

    protected sealed override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        yield return EvaluateValue(input, context, environment);
    }

    protected abstract JsonNode? EvaluateValue(JsonNode? input, JqContext context, JqEnvironment environment);
}

internal sealed class IdentityFilter : SingleValueFilter
{
    protected override JsonNode? EvaluateValue(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        return input;
    }

    // Upstream DUP shares the input pointer, and path-mode values are
    // read-only carriers: update spines are cloned before ApplyFrame mutates
    // them, and CollectPaths only keeps segments. Yielding the pair itself
    // lets as-bindings observe the same reference the body input carries,
    // which the variable identity rule below depends on.
    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        yield return pair;
    }
}

internal sealed class LiteralFilter(JsonNode? value) : SingleValueFilter
{
    internal JsonNode? Value => value;

    protected override JsonNode? EvaluateValue(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        return value;
    }
}

internal sealed class VariableFilter(string name) : SingleValueFilter
{
    protected override JsonNode? EvaluateValue(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (!environment.TryGetValue(name, out JsonNode? value))
            throw new JqException($"undefined variable ${name}");
        return value;
    }

    // Upstream LOADV pushes the stored pointer without touching path state,
    // and PATH_END compares it against value_at_path by jv_identical, which
    // is pointer equality for heap values (execute.c LOADV/STOREV with
    // path_intact; jv.c jv_identical). Bindings store references (see Match),
    // so a variable holding the current path value keeps the incoming
    // segments and tracking. Other immediate values compare by value;
    // other heap values remain borrowed but travel untracked.
    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        if (!environment.TryGetValue(name, out JsonNode? value))
            throw new JqException($"undefined variable ${name}");
        yield return TrackResult(pair, value);
    }
}

// Source location: a parse-time constant capturing the file and line where
// `$__loc__` occurs. The file is the module display path or `<top-level>`
// for inline filters; the line is 1-based.
internal sealed class LocFilter(string file, int line) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        var location = new JsonObject
        {
            ["file"] = JsonValue.Create(file),
            ["line"] = JsonValue.Create(line),
        };
        context.Budget.CheckCancellation();
        context.Budget.ReserveString(file.Length + 8);
        yield return location;
    }
}

// Lexical bindings: each source value extends the environment through the
// first matching alternative, then runs the body against the outer input.
// Later alternatives run only when the pattern or body reports a catchable
// error; final errors propagate.
internal sealed class AsFilter(
    JqFilter source,
    IReadOnlyList<BindingPattern> alternatives,
    JqFilter body) : JqFilter
{
    private readonly HashSet<string> _allNames = CollectAll(alternatives);

    internal static HashSet<string> CollectAll(IReadOnlyList<BindingPattern> alternatives)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (BindingPattern alternative in alternatives)
            alternative.CollectBoundNames(names);
        return names;
    }

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (JsonNode? bound in source.Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (var (_, output) in DriveAlternatives(alternatives, _allNames, bound, environment, context, scope => body.Evaluate(input, context, scope)).ConfigureAwait(false))
                yield return output;
    }

    // Shared alternative driver for bindings and reductions: every name
    // starts null, the first non-erroring alternative wins (even when it
    // yields nothing), and catchable match or body errors retry later
    // alternatives. Reduction drivers reuse it with collecting bodies.
    internal static async IAsyncEnumerable<(JqEnvironment Scope, JsonNode? Output)> DriveAlternatives(IReadOnlyList<BindingPattern> alternatives, HashSet<string> allNames, JsonNode? bound, JqEnvironment environment, JqContext context, Func<JqEnvironment, IAsyncEnumerable<JsonNode?>> body)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        ArgumentNullException.ThrowIfNull(allNames);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        using var storage = new JqScopeStorage(context.Budget);
        JqEnvironment prebound = Prebind(alternatives, allNames, environment, context, storage);
        bool completed = false;
        for (int index = 0; index < alternatives.Count && !completed; index++)
        {
            bool isLast = index == alternatives.Count - 1;
            var scopes = Match(alternatives[index], bound, prebound, context, storage, 1).GetAsyncEnumerator();
            await using var scopesLifetime = scopes.ConfigureAwait(false);
            while (true)
            {
                bool moved;
                try
                {
                    moved = await scopes.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (JqErrors.IsCatchable(exception))
                {
                    if (isLast)
                        throw;
                    break;
                }
                if (!moved)
                {
                    completed = true;
                    break;
                }
                bool bodyFailed = false;
                var results = body(scopes.Current).GetAsyncEnumerator();
                await using var resultsLifetime = results.ConfigureAwait(false);
                while (true)
                {
                    bool produced;
                    try
                    {
                        produced = await results.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (JqErrors.IsCatchable(exception))
                    {
                        if (isLast)
                            throw;
                        bodyFailed = true;
                        break;
                    }
                    if (!produced)
                        break;
                    yield return (scopes.Current, results.Current);
                }
                if (bodyFailed)
                    break;
            }
        }
    }

    private static JqEnvironment Prebind(IReadOnlyList<BindingPattern> alternatives, HashSet<string> names, JqEnvironment environment, JqContext context, JqScopeStorage storage)
    {
        // A single variable always binds before the body and has no key
        // expressions that could observe a provisional null binding.
        if (alternatives.Count == 1 && alternatives[0] is VariablePattern) return environment;
        storage.Reserve(128L * names.Count, names.Count);
        foreach (string name in names)
        {
            context.Budget.CheckCancellation();
            environment = environment.Extend(name, null);
        }
        return environment;
    }

    private static async IAsyncEnumerable<JqEnvironment> Match(BindingPattern pattern, JsonNode? value, JqEnvironment scope, JqContext context, JqScopeStorage storage, int depth)
    {
        context.Budget.CheckCancellation();
        JqRecursion.CheckDepth(depth, "pattern nesting limit exceeded");
        switch (pattern)
        {
            case VariablePattern variable:
                storage.Reserve(128, 1);
                try { yield return scope.Extend(variable.Name, value); }
                finally { storage.Release(128); }
                break;
            case AliasPattern alias:
                storage.Reserve(128, 1);
                try
                {
                    JqEnvironment aliased = scope.Extend(alias.Name, value);
                    await foreach (JqEnvironment inner in Match(alias.Inner, value, aliased, context, storage, depth + 1).ConfigureAwait(false))
                        yield return inner;
                }
                finally { storage.Release(128); }
                break;
            case ArrayPattern array:
                await foreach (JqEnvironment bound in (MatchSequence(array.Items.Count, scope, context, storage,
                    (index, current) => Match(array.Items[index], JqPathReads.ReadIndex(value, index, false, context), current, context, storage, depth + 1))).ConfigureAwait(false))
                    yield return bound;
                break;
            case ObjectPattern obj:
                await foreach (JqEnvironment bound in (MatchSequence(obj.Properties.Count, scope, context, storage,
                    (index, current) => MatchProperty(obj.Properties[index], current))).ConfigureAwait(false))
                    yield return bound;
                break;
            default:
                throw new InvalidOperationException("Unknown binding pattern.");
        }

        async IAsyncEnumerable<JqEnvironment> MatchProperty(ObjectPatternProperty property, JqEnvironment current)
        {
            await foreach (JsonNode? keyValue in property.Key.Evaluate(value, context, current).ConfigureAwait(false))
            {
                if (!TryGetString(keyValue, out string key))
                    throw new JqRuntimeException($"Cannot use {TypeName(keyValue)} ({context.Runtime.TruncatedDump(keyValue)}) as object key");
                JsonNode? field = JqPathReads.ReadField(value, key, context.Runtime);
                await foreach (JqEnvironment bound in Match(property.Value, field, current, context, storage, depth + 1).ConfigureAwait(false))
                    yield return bound;
            }
        }
    }

    private static async IAsyncEnumerable<JqEnvironment> MatchSequence(int count, JqEnvironment scope, JqContext context, JqScopeStorage storage,
        Func<int, JqEnvironment, IAsyncEnumerable<JqEnvironment>> step)
    {
        if (count == 0)
        {
            yield return scope;
            yield break;
        }
        long bytes = 64 + 128L * count;
        storage.Reserve(bytes, count);
        var iterators = new IAsyncEnumerator<JqEnvironment>?[count];
        JqEnvironment current = scope;
        int index = 0;
        try
        {
            while (index >= 0)
            {
                context.Budget.CheckCancellation();
                if (index == count)
                {
                    yield return current;
                    index--;
                    continue;
                }
                IAsyncEnumerator<JqEnvironment> active = iterators[index] ?? step(index, current).GetAsyncEnumerator();
                iterators[index] = active;
                if (await context.Budget.MoveNextAsync(active).ConfigureAwait(false))
                {
                    current = active.Current;
                    index++;
                    continue;
                }
                iterators[index] = null;
                await active.DisposeAsync().ConfigureAwait(false);
                index--;
            }
        }
        finally
        {
            try
            {
                for (int remaining = count - 1; remaining >= 0; remaining--)
                    if (iterators[remaining] is { } remainingIterator) await remainingIterator.DisposeAsync().ConfigureAwait(false);
            }
            finally { storage.Release(bytes); }
        }
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        // The source binds in path mode while the body extends the incoming
        // path against the outer input. Upstream wraps the binding source in
        // a subexpression (compile.c gen_destructure via gen_subexp), so the
        // source leaves path state alone and the stored pointer is compared
        // against the outer value downstream, which is what the reference
        // check in the variable rule reproduces.
        using var storage = new JqScopeStorage(context.Budget);
        JqEnvironment prebound = Prebind(alternatives, _allNames, environment, context, storage);
        await foreach (JqValuePath sourcePair in source.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
        {
            JsonNode? bound = sourcePair.Value;
            bool completed = false;
            for (int index = 0; index < alternatives.Count && !completed; index++)
            {
                bool isLast = index == alternatives.Count - 1;
                var results = RunPathAlternative(alternatives[index], bound, prebound, pair, context, storage).GetAsyncEnumerator();
                await using var resultsLifetime = results.ConfigureAwait(false);
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await results.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (JqErrors.IsCatchable(exception))
                    {
                        if (isLast)
                            throw;
                        break;
                    }
                    if (!moved)
                    {
                        completed = true;
                        break;
                    }
                    yield return results.Current;
                }
            }
        }
    }

    private async IAsyncEnumerable<JqValuePath> RunPathAlternative(BindingPattern alternative, JsonNode? bound, JqEnvironment prebound, JqValuePath pair, JqContext context, JqScopeStorage storage)
    {
        await foreach (JqEnvironment scope in Match(alternative, bound, prebound, context, storage, 1).ConfigureAwait(false))
            await foreach (JqValuePath next in body.EvaluatePaths(pair, context, scope).ConfigureAwait(false))
                yield return next;
    }
}

// A `def name[(params)]: body; rest` definition: forges no values itself but
// extends the environment with the definition before running the rest.
// Its closure captures this extended environment once, so each `def`
// observes exactly the bindings visible at its own site.
internal sealed class DefFilter(JqFunctionDefinition Definition, JqFilter Continuation) : JqFilter
{
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        Run(environment, context, (scope, continuation) => continuation.Evaluate(input, context, scope));

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        Run(environment, context, (scope, continuation) => continuation.EvaluatePaths(pair, context, scope));

    private async IAsyncEnumerable<T> Run<T>(JqEnvironment environment, JqContext context,
        Func<JqEnvironment, JqFilter, IAsyncEnumerable<T>> evaluate)
    {
        JqScopeStorage? storage = new JqScopeStorage(context.Budget);
        try
        {
            var (extended, continuation) = Bind(environment, context, storage);
            var results = evaluate(extended, continuation).GetAsyncEnumerator();
            await using var resultsLifetime = results.ConfigureAwait(false);
            while (true)
            {
                bool moved;
                try { moved = await results.MoveNextAsync().ConfigureAwait(false); }
                catch (TailCallSignal next)
                {
                    next.RetainScope(storage);
                    storage = null;
                    throw;
                }
                if (!moved) yield break;
                yield return results.Current;
            }
        }
        finally { storage?.Dispose(); }
    }

    internal JqFunctionDefinition FunctionDefinition => Definition;

    private (JqEnvironment Environment, JqFilter Continuation) Bind(JqEnvironment environment, JqContext context, JqScopeStorage storage)
    {
        int count = 0;
        for (JqFilter probe = this; probe is DefFilter definition; probe = definition.ContinuationBody)
        {
            context.Budget.CheckCancellation();
            count = checked(count + 1);
        }
        storage.Reserve(160L * count, (int)Math.Min(32, 2L * count));
        JqFilter current = this;
        while (current is DefFilter definition)
        {
            context.Budget.CheckCancellation();
            var function = definition.FunctionDefinition;
            environment = environment.ExtendFunction(function);
            current = definition.ContinuationBody;
        }
        return (environment, current);
    }

    internal JqFilter ContinuationBody => Continuation;

    internal DefFilter WithContinuation(JqFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new DefFilter(Definition, next);
    }
}

// A call to a user-defined function. Value arguments stream at the call
// site (cartesian combinations drive one body run each, first-argument-outer
// like the reference range vectors); filter arguments are captured with
// the caller environment and re-evaluated afresh at every use. The body
// runs against the call input in the closure environment, never the
// caller's later bindings.
internal sealed class UserCallFilter(string Name, int Arity, IReadOnlyList<JqFilter> Args) : JqFilter
{
    internal override bool IsSingleValued(JqEnvironment environment, int depth) =>
        // Prove a directly called simple definition, without expanding its call graph.
        depth == 0 && Args.All(arg => arg.IsSingleValued(environment, depth + 1))
        && environment.TryGetFunctionQualified(Name, Arity, out JqUserClosure? closure) && closure is not null
        && closure.Definition.Body.IsSingleValued(closure.Environment, depth + 1);

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetFunctionQualified(Name, Arity, out JqUserClosure? closure) || closure is null)
            throw new JqException($"undefined function {Name}/{Arity}");
        if (Args.Count != closure.Definition.Arity)
            throw new JqException($"undefined function {Name}/{Arity}");
        await foreach (JsonNode? value in EvaluateCallLoop(closure, input, Args, environment, context).ConfigureAwait(false))
            yield return value;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetFunctionQualified(Name, Arity, out JqUserClosure? closure) || closure is null)
            throw new JqException($"undefined function {Name}/{Arity}");
        if (Args.Count != closure.Definition.Arity)
            throw new JqException($"undefined function {Name}/{Arity}");
        await foreach (JqValuePath next in EvaluatePathsCallLoop(closure, pair, Args, environment, context).ConfigureAwait(false))
            yield return next;
    }

    internal string FunctionName => Name;
    internal int FunctionArity => Arity;
    internal IReadOnlyList<JqFilter> CallArgs => Args;

    private sealed record CallFrame(JsonNode? Input, JsonNode?[] Values, IReadOnlyList<int> Positions,
        JqFilterClosure?[] Filters, JqValuePath? Path);

    private sealed class CallCursor(IAsyncEnumerator<CallFrame> frames, TailCallSignal request) : IAsyncEnumerator<CallFrame>
    {
        public CallFrame Current => frames.Current;
        public ValueTask<bool> MoveNextAsync() => frames.MoveNextAsync();
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        public async ValueTask DisposeAsync()
        {
            try { await frames.DisposeAsync().ConfigureAwait(false); }
            finally { request.ReleaseScopes(); }
        }
    }

    internal static IAsyncEnumerable<JsonNode?> EvaluateCallLoop(JqUserClosure closure, JsonNode? input,
        IReadOnlyList<JqFilter> args, JqEnvironment environment, JqContext context) =>
        RunCalls(closure, new TailCallSignal(closure.Definition, input, environment, args, null, null), context,
            frame => closure.Definition.Body.Evaluate(frame.Input, context, Bind(frame, closure)));

    internal static IAsyncEnumerable<JqValuePath> EvaluatePathsCallLoop(JqUserClosure closure, JqValuePath pair,
        IReadOnlyList<JqFilter> args, JqEnvironment environment, JqContext context) =>
        RunCalls(closure, new TailCallSignal(closure.Definition, pair.Value, environment, args, null, pair), context,
            frame => closure.Definition.Body.EvaluatePaths(frame.Path ?? throw new InvalidOperationException("Missing call path."),
                context, Bind(frame, closure)));

    private static JqEnvironment Bind(CallFrame frame, JqUserClosure closure)
    {
        JqEnvironment scope = closure.Environment;
        for (int i = 0; i < frame.Positions.Count; i++)
            scope = scope.Extend(closure.Definition.Parameters[frame.Positions[i]].Name, frame.Values[i]);
        for (int i = 0; i < frame.Filters.Length; i++)
            if (frame.Filters[i] is JqFilterClosure filter)
                scope = scope.ExtendFilter(closure.Definition.Parameters[i].Name, filter);
        return scope;
    }

    // The driver owns suspended sources and argument cursors. Proven single
    // calls replace their predecessor without probing for another result.
    private static async IAsyncEnumerable<T> RunCalls<T>(JqUserClosure closure, TailCallSignal request, JqContext context,
        Func<CallFrame, IAsyncEnumerable<T>> evaluate)
    {
        JqFunctionDefinition definition = closure.Definition;
        using var ownedStorage = context.Budget.TemporaryStorage is null ? new JqStorage(context.Budget.MemoryBudget) : null;
        JqStorage storage = context.Budget.TemporaryStorage ?? ownedStorage
            ?? throw new InvalidOperationException("Missing call storage owner.");
        long arenaBytes = 0;
        long capturedBytes = 0;
        JqScopeStorage? capturedScopes = null;
        var pending = new List<(IAsyncEnumerator<CallFrame> Cursor, bool Single)>();
        IAsyncEnumerator<CallFrame>? current = null;
        IAsyncEnumerator<T>? outputs = null;
        bool single = false;
        try
        {
            Start(request);
            while (await Advance().ConfigureAwait(false))
            {
                if (outputs is null) throw new InvalidOperationException("Missing call output cursor.");
                yield return outputs.Current;
            }
        }
        finally
        {
            try { await DisposeAll(null).ConfigureAwait(false); }
            finally
            {
                try { JqScopeStorage.DisposeAll(capturedScopes); }
                finally { storage.DiscardBytes(checked(arenaBytes + capturedBytes)); }
            }
        }

        void Start(TailCallSignal next)
        {
            try
            {
                if (next.Args.Count != definition.Arity)
                    throw new JqException($"undefined function {definition.Name}/{definition.Arity}");
                if (pending.Count + 2 > pending.Capacity)
                {
                    int capacity = checked(Math.Max(2, pending.Capacity * 2));
                    long bytes = checked(capacity * (1024L + 256L * definition.Arity));
                    context.Budget.CheckCancellation();
                    storage.ReserveBytes(bytes, capacity);
                    try { context.Budget.CheckCancellation(); pending.Capacity = capacity; }
                    catch { storage.DiscardBytes(bytes); throw; }
                    storage.DiscardBytes(arenaBytes);
                    arenaBytes = bytes;
                }
                single = next.InputSource is null || next.InputSource.IsSingleValued(next.Environment, 0);
                for (int i = 0; i < definition.Arity && single; i++)
                    if (definition.Parameters[i].IsValue)
                        single = next.Args[i].IsSingleValued(next.Environment, 0);
                IAsyncEnumerator<CallFrame> frames = Frames(next).GetAsyncEnumerator();
                current = next.HasScopes ? new CallCursor(frames, next) : frames;
            }
            catch { next.ReleaseScopes(); throw; }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        async ValueTask<bool> Advance()
        {
            try
            {
                while (true)
                {
                    context.Budget.CheckCancellation();
                    if (outputs is not null)
                    {
                        bool moved;
                        try { moved = await outputs.MoveNextAsync().ConfigureAwait(false); }
                        catch (TailCallSignal next) when (next.Definition.Name == definition.Name && next.Definition.Arity == definition.Arity)
                        {
                            try
                            {
                                IAsyncEnumerator<T> completed = outputs;
                                outputs = null;
                                await completed.DisposeAsync().ConfigureAwait(false);
                                if (current is not null)
                                {
                                    if (single)
                                    {
                                        IAsyncEnumerator<CallFrame> exhausted = current;
                                        current = null;
                                        await exhausted.DisposeAsync().ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        pending.Add((current, single));
                                        current = null;
                                    }
                                }
                                Start(next);
                            }
                            catch { next.ReleaseScopes(); throw; }
                            continue;
                        }
                        if (moved) return true;
                        IAsyncEnumerator<T> exhaustedOutputs = outputs;
                        outputs = null;
                        await exhaustedOutputs.DisposeAsync().ConfigureAwait(false);
                    }
                    if (current is null)
                    {
                        if (pending.Count == 0) return false;
                        (current, single) = pending[^1];
                        pending.RemoveAt(pending.Count - 1);
                    }
                    if (await current.MoveNextAsync().ConfigureAwait(false))
                        outputs = evaluate(current.Current).GetAsyncEnumerator();
                    else
                    {
                        IAsyncEnumerator<CallFrame> exhausted = current;
                        current = null;
                        await exhausted.DisposeAsync().ConfigureAwait(false);
                    }
                }
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
            await Dispose(outputs).ConfigureAwait(false);
            outputs = null;
            await Dispose(current).ConfigureAwait(false);
            current = null;
            for (int i = pending.Count - 1; i >= 0; i--) await Dispose(pending[i].Cursor).ConfigureAwait(false);
            pending.Clear();
            if (failure is not null && !ReferenceEquals(failure, primary))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
            async ValueTask Dispose(IAsyncDisposable? cursor)
            {
                try { if (cursor is not null) await cursor.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    if (failure is null || JqErrors.IsCatchable(failure) && !JqErrors.IsCatchable(exception))
                        failure = exception;
                }
            }
        }

        async IAsyncEnumerable<CallFrame> Frames(TailCallSignal next)
        {
            // Direct forwarding borrows the original closure; it does not retain
            // another invocation environment. Only new lexical expressions can
            // keep the preceding frame alive after a tail replacement.
            if (!ReferenceEquals(next, request))
            {
                for (int i = 0; i < definition.Arity; i++)
                {
                    context.Budget.CheckCancellation();
                    if (!definition.Parameters[i].IsValue
                        && next.Args[i] is not (FilterParamCallFilter or IdentityFilter or LiteralFilter))
                    {
                        long bytes = 256L + 192L * definition.Arity;
                        storage.ReserveBytes(bytes, definition.Arity + 1);
                        capturedBytes = checked(capturedBytes + bytes);
                        context.Budget.CheckCancellation();
                        next.TransferScopes(ref capturedScopes);
                        break;
                    }
                }
            }
            var values = new List<JqFilter>();
            var positions = new List<int>();
            var filters = new JqFilterClosure?[definition.Arity];
            for (int i = 0; i < definition.Arity; i++)
            {
                context.Budget.CheckCancellation();
                if (definition.Parameters[i].IsValue)
                {
                    values.Add(next.Args[i]);
                    positions.Add(i);
                }
                else filters[i] = next.Args[i] switch
                {
                    FilterParamCallFilter forwarded => forwarded.Resolve(next.Environment),
                    IdentityFilter or LiteralFilter => new JqFilterClosure(next.Args[i], context.RootEnvironment),
                    _ => new JqFilterClosure(next.Args[i], next.Environment),
                };
            }
            await foreach (var source in Sources().ConfigureAwait(false))
                await foreach (JsonNode?[] combo in Combine(source.Value).ConfigureAwait(false))
                    yield return new CallFrame(source.Value, combo, positions, filters, source.Path);

            async IAsyncEnumerable<(JsonNode? Value, JqValuePath? Path)> Sources()
            {
                if (next.InputSource is not JqFilter source)
                    yield return (next.Input, next.Path);
                else if (next.Path is JqValuePath path)
                    await foreach (JqValuePath value in source.EvaluatePaths(path, context, next.Environment).ConfigureAwait(false))
                        yield return (value.Value, value);
                else
                    await foreach (JsonNode? value in source.Evaluate(next.Input, context, next.Environment).ConfigureAwait(false))
                        yield return (value, null);
            }

            // First argument varies slowest. One reusable vector and iterative
            // cursors avoid both a cartesian inventory and an arity-sized C# stack.
            async IAsyncEnumerable<JsonNode?[]> Combine(JsonNode? input)
            {
                var currentValues = new JsonNode?[values.Count];
                if (values.Count == 0)
                {
                    yield return currentValues;
                    yield break;
                }
                var iterators = new IAsyncEnumerator<JsonNode?>?[values.Count];
                int index = 0;
                Exception? primary = null;
                try
                {
                    while (index >= 0)
                    {
                        if (index == values.Count)
                        {
                            yield return currentValues;
                            index--;
                            continue;
                        }
                        try
                        {
                            context.Budget.CheckCancellation();
                            IAsyncEnumerator<JsonNode?> iterator = iterators[index] ??=
                                values[index].Evaluate(input, context, next.Environment).GetAsyncEnumerator();
                            if (await iterator.MoveNextAsync().ConfigureAwait(false))
                            {
                                currentValues[index] = iterator.Current;
                                index++;
                            }
                            else
                            {
                                iterators[index] = null;
                                currentValues[index] = null;
                                await iterator.DisposeAsync().ConfigureAwait(false);
                                index--;
                            }
                        }
                        catch (Exception exception)
                        {
                            primary = exception;
                            throw;
                        }
                    }
                }
                finally
                {
                    Exception? failure = primary;
                    for (int i = iterators.Length - 1; i >= 0; i--)
                    {
                        try { if (iterators[i] is { } remainingIterator) await remainingIterator.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception)
                        {
                            if (failure is null || JqErrors.IsCatchable(failure) && !JqErrors.IsCatchable(exception))
                                failure = exception;
                        }
                    }
                    if (failure is not null && !ReferenceEquals(failure, primary))
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }
        }
    }
}

// A use of a filter parameter: re-evaluates the captured argument against
// the current input in the caller environment from the call site.
internal sealed class FilterParamCallFilter(string Name) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JqFilterClosure closure = Resolve(environment);
        await foreach (JsonNode? value in closure.Filter.Evaluate(input, context, closure.Environment).ConfigureAwait(false))
            yield return value;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JqFilterClosure closure = Resolve(environment);
        await foreach (JqValuePath next in closure.Filter.EvaluatePaths(pair, context, closure.Environment).ConfigureAwait(false))
            yield return next;
    }

    internal JqFilterClosure Resolve(JqEnvironment environment)
    {
        if (!environment.TryGetFilter(Name, out JqFilterClosure? closure) || closure is null)
            throw new JqException($"undefined filter {Name}");
        return closure;
    }
}

// A tail-position self-call unwinds to the owning call loop instead of
// nesting another evaluator frame, so tail-recursive runs take heap and
// budget rather than C# stack. Deliberately not a JqException: quota,
// cancellation, and language-error boundaries must never observe it.
internal sealed class TailCallSignal(JqFunctionDefinition definition, JsonNode? input, JqEnvironment environment,
    IReadOnlyList<JqFilter> args, JqFilter? source, JqValuePath? path) : Exception
{
    private JqScopeStorage? _scopes;

    internal bool HasScopes => _scopes is not null;

    internal void RetainScope(JqScopeStorage scope)
    {
        scope.Next = _scopes;
        _scopes = scope;
    }

    internal void TransferScopes(ref JqScopeStorage? captured)
    {
        while (_scopes is JqScopeStorage scope)
        {
            _scopes = scope.Next;
            scope.Next = captured;
            captured = scope;
        }
    }

    internal void ReleaseScopes()
    {
        JqScopeStorage? scopes = _scopes;
        _scopes = null;
        JqScopeStorage.DisposeAll(scopes);
    }

    internal JqFunctionDefinition Definition { get; } = definition;
    internal JsonNode? Input { get; } = input;
    internal JqEnvironment Environment { get; } = environment;
    internal IReadOnlyList<JqFilter> Args { get; } = args;
    internal JqFilter? InputSource { get; } = source;
    internal JqValuePath? Path { get; } = path;
}

internal sealed class TailSelfCallFilter(JqFunctionDefinition Definition, IReadOnlyList<JqFilter> Args) : JqFilter
{
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        throw new TailCallSignal(Definition, input, environment, Args, null, null);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        throw new TailCallSignal(Definition, pair.Value, environment, Args, null, pair);

}

internal sealed class PipeFilter(JqFilter left, JqFilter right) : JqFilter
{
    internal override bool IsSingleValued(JqEnvironment environment, int depth) =>
        depth < JqRecursion.MaximumDepth && left.IsSingleValued(environment, depth + 1)
        && right.IsSingleValued(environment, depth + 1);
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var value in left.Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (var output in right.Evaluate(value, context, environment).ConfigureAwait(false))
                yield return output;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValuePath left in left.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
            await foreach (JqValuePath right in right.EvaluatePaths(left, context, environment).ConfigureAwait(false))
                yield return right;
    }

    internal JqFilter Left => left;

    internal JqFilter Right => right;

    internal PipeFilter WithRight(JqFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new PipeFilter(left, next);
    }
}

// Source iteration transfers to the call driver before it starts. Pending
// siblings remain owned there while the selected call runs, without lookahead.
internal sealed class PipeTailLoop(JqFilter Source, JqFunctionDefinition Definition, IReadOnlyList<JqFilter> Args) : JqFilter
{
    protected override IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment) =>
        throw new TailCallSignal(Definition, input, environment, Args, Source, null);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        throw new TailCallSignal(Definition, pair.Value, environment, Args, Source, pair);
}

internal sealed class CommaFilter(JqFilter left, JqFilter right) : JqFilter
{
    internal JqFilter Left => left;
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var value in left.Evaluate(input, context, environment).ConfigureAwait(false))
            yield return value;
        await foreach (var value in right.Evaluate(input, context, environment).ConfigureAwait(false))
            yield return value;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValuePath left in left.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
            yield return left;
        await foreach (JqValuePath right in right.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
            yield return right;
    }

    internal JqFilter Right => right;

    internal CommaFilter WithRight(JqFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new CommaFilter(left, next);
    }
}

internal sealed class FieldFilter(JqFilter source, string name, bool optional) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var value in source.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (optional && value is not null && value is not JsonObject) continue;
            yield return JqPathReads.ReadField(value, name, context.Runtime);
        }
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValuePath source in source.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
        {
            RequireTracked(source, new KeySegment(name), context);
            if (optional && source.Value is not null && source.Value is not JsonObject) continue;
            JsonNode? field = JqPathReads.ReadField(source.Value, name, context.Runtime);
            yield return new JqValuePath(Extend(source.Segments, new KeySegment(name), context.Budget), field, true);
        }
    }
}

internal sealed class IndexFilter(JqFilter source, JqFilter index, bool optional) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (JsonNode? key in index.Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JsonNode? value in source.Evaluate(input, context, environment).ConfigureAwait(false))
                if (JqPathReads.TryIndex(value, key, optional, context, out JsonNode? result))
                    yield return result;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        // Keys are subexpressions of the enclosing input, before the source.
        // Optional access suppresses read failures, never key/source effects.
        await foreach (JsonNode? key in index.Evaluate(outer.Value, context, environment).ConfigureAwait(false))
            await foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment).ConfigureAwait(false))
            {
                if (!pair.Tracked) RequireTracked(pair, PathSegmentFor(key, context), context);
                if (JqPathReads.TryIndex(pair.Value, key, optional, context, out JsonNode? result))
                    yield return new JqValuePath(Extend(pair.Segments, PathSegmentFor(key, context), context.Budget), result, true);
            }
    }

    private static JqValueSegment PathSegmentFor(JsonNode? key, JqContext context)
    {
        if (TryGetString(key, out string? name)) return new KeySegment(name);
        if (JqPaths.TryGetFractional(key, out double fractional)) return new FractionalSegment(fractional);
        if (TryGetIndex(key, out long index, out bool isNaN)) return new IndexSegment(index, isNaN);
        return new InvalidSegment(context.Budget.CopyValue(key, 0));
    }
}

internal sealed class SliceFilter(JqFilter source, JqFilter? start, JqFilter? end, bool optional) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (JsonNode? lower in Bounds(start, input, context, environment).ConfigureAwait(false))
            await foreach (JsonNode? upper in Bounds(end, input, context, environment).ConfigureAwait(false))
                await foreach (JsonNode? value in source.Evaluate(input, context, environment).ConfigureAwait(false))
                    if (JqPathReads.TrySlice(value, lower, upper, optional, context, out JsonNode? result))
                        yield return result;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        await foreach (JsonNode? lower in Bounds(start, outer.Value, context, environment).ConfigureAwait(false))
            await foreach (JsonNode? upper in Bounds(end, outer.Value, context, environment).ConfigureAwait(false))
                await foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment).ConfigureAwait(false))
                {
                    if (JqPathReads.TrySlicePath(pair, lower, upper, optional, context, out JqValuePath? result))
                        yield return result;
                }
    }

    private static async IAsyncEnumerable<JsonNode?> Bounds(JqFilter? bound, JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (bound is null) yield return null;
        else
            await foreach (JsonNode? value in bound.Evaluate(input, context, environment).ConfigureAwait(false))
                yield return value;
    }
}

internal sealed class IteratorFilter(JqFilter source, bool optional) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var value in source.Evaluate(input, context, environment).ConfigureAwait(false))
            foreach (JsonNode? child in ReadValues(value, optional, context.Budget))
                yield return child;
    }

    internal static IEnumerable<JsonNode?> ReadValues(JsonNode? value, bool optional, JqBudget budget)
    {
        budget.CheckCancellation();
        if (value is JsonArray arr)
        {
            foreach (JsonNode? child in arr)
            {
                budget.CheckCancellation();
                yield return child;
            }
        }
        else if (value is JsonObject obj)
        {
            foreach (var child in obj)
            {
                budget.CheckCancellation();
                yield return child.Value;
            }
        }
        else if (!optional)
            throw new JqRuntimeException($"cannot iterate over {TypeName(value)}");
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment).ConfigureAwait(false))
            foreach (JqValuePath child in ReadPaths(pair, context))
                yield return child;
    }

    internal static IEnumerable<JqValuePath> ReadPaths(JqValuePath pair, JqContext context)
    {
        if ((pair.Value is JsonArray || pair.Value is JsonObject) && !pair.Tracked)
            throw new JqException(InvalidIterate(pair.Value, context));
        if (pair.Value is JsonArray arr)
        {
            for (int index = 0; index < arr.Count; index++)
                yield return new JqValuePath(Extend(pair.Segments, new IndexSegment(index, false), context.Budget), arr[index], true);
        }
        else if (pair.Value is JsonObject obj)
        {
            foreach (var property in obj)
                yield return new JqValuePath(Extend(pair.Segments, new KeySegment(property.Key), context.Budget), property.Value, true);
        }
        else
        {
            if (!pair.Tracked)
                throw new JqException(InvalidIterate(pair.Value, context));
            throw new JqRuntimeException($"cannot iterate over {TypeName(pair.Value)}");
        }
    }
}

// Pre-order depth-first traversal: the value itself, then each child in
// order, matching recursive descent through iteration.
internal sealed class RecursiveDescentFilter : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        yield return input;
        using var cursor = new JqTreeCursor(context.Budget);
        JsonNode? current = input;
        while (true)
        {
            if (current is JsonArray { Count: > 0 } or JsonObject { Count: > 0 }) cursor.Push(current);
            while (cursor.Count != 0 && !cursor.TryNext(out current)) cursor.Pop();
            if (cursor.Count == 0) yield break;
            yield return current;
        }
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        yield return pair;
        if (pair.Value is not null && !pair.Tracked)
            throw new JqException(InvalidIterate(pair.Value, context));
        using var cursor = new JqTreeCursor(context.Budget);
        JsonNode? current = pair.Value;
        while (true)
        {
            if (current is JsonArray { Count: > 0 } or JsonObject { Count: > 0 }) cursor.Push(current);
            while (cursor.Count != 0 && !cursor.TryNext(out current)) cursor.Pop();
            if (cursor.Count == 0) yield break;
            yield return new JqValuePath(cursor.CopySegments(pair.Segments), current, true);
        }
    }
}

internal sealed class ArrayFilter(JqFilter item) : JqFilter
{
    internal JqFilter Item => item;

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        context.Budget.ReserveBytes(64);
        var array = new JsonArray();
        await foreach (var value in item.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            context.Budget.ReserveBytes(value is JsonValue ? 88 : 24);
            array.Add(context.Runtime.Clone(value, 1));
        }
        yield return array;
    }
}

internal sealed record ObjectProperty(string? StaticKey, JqFilter? KeyFilter, JqFilter Value);

internal sealed class ObjectFilter(IReadOnlyList<ObjectProperty> properties) : JqFilter
{
    internal IReadOnlyList<ObjectProperty> Properties => properties;

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (properties.Count == 0)
        {
            context.Budget.ReserveBytes(64);
            yield return new JsonObject();
            yield break;
        }
        using var workspace = new JqScopeStorage(context.Budget);
        workspace.Reserve(128L * properties.Count, properties.Count);
        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var iterators = new IAsyncEnumerator<(string Key, JsonNode? Value)>?[properties.Count];
        var keys = new string?[properties.Count];
        var previous = new (bool Exists, JsonNode? Value)[properties.Count];
        int index = 0;
        try
        {
            while (index >= 0)
            {
                context.Budget.CheckCancellation();
                if (keys[index] is { } oldKey)
                {
                    RestoreField(oldKey, previous[index]);
                    keys[index] = null;
                }
                var iterator = iterators[index] ??= Choices(properties[index]).GetAsyncEnumerator();
                if (!await iterator.MoveNextAsync().ConfigureAwait(false))
                {
                    await iterator.DisposeAsync().ConfigureAwait(false);
                    iterators[index] = null;
                    index--;
                    continue;
                }
                var choice = iterator.Current;
                previous[index] = (fields.TryGetValue(choice.Key, out var oldValue), oldValue);
                fields[choice.Key] = choice.Value;
                keys[index] = choice.Key;
                if (index + 1 < properties.Count) { index++; continue; }
                context.Budget.ReserveBytes(64 + 96L * fields.Count);
                context.Budget.ReserveBytes(64L * fields.Values.Count(value => value is JsonValue));
                var result = new JsonObject();
                foreach (var field in fields)
                    result.Add(field.Key, context.Budget.CopyValue(field.Value, 1));
                yield return result;
            }
        }
        finally
        {
            for (int pending = iterators.Length - 1; pending >= 0; pending--)
                if (iterators[pending] is { } remainingIterator) await remainingIterator.DisposeAsync().ConfigureAwait(false);
        }

        async IAsyncEnumerable<(string Key, JsonNode? Value)> Choices(ObjectProperty property)
        {
            if (property is { StaticKey: string key, KeyFilter: null })
            {
                await foreach (var value in Values(property.Value).ConfigureAwait(false)) yield return (key, value);
            }
            else if (property.KeyFilter is JqFilter keyFilter)
            {
                await foreach (var keyValue in Values(keyFilter).ConfigureAwait(false))
                {
                    if (!TryGetString(keyValue, out string keyName))
                        throw new JqRuntimeException($"Cannot use {TypeName(keyValue)} ({context.Runtime.TruncatedDump(keyValue)}) as object key");
                    await foreach (var value in Values(property.Value).ConfigureAwait(false)) yield return (keyName, value);
                }
            }
        }

        async IAsyncEnumerable<JsonNode?> Values(JqFilter filter)
        {
            // Scalar leaves need no suspended evaluator frame while later
            // properties run. Complete their single-value iterator first.
            if (filter is SingleValueFilter)
            {
                JsonNode? value = await filter.Evaluate(input, context, environment).SingleAsync().ConfigureAwait(false);
                yield return value;
                yield break;
            }
            await foreach (JsonNode? value in filter.Evaluate(input, context, environment).ConfigureAwait(false))
                yield return value;
        }

        void RestoreField(string key, (bool Exists, JsonNode? Value) old)
        {
            if (old.Exists) fields[key] = old.Value;
            else fields.Remove(key);
        }
    }
}

internal sealed class BinaryFilter(JqFilter left, string op, JqFilter right) : JqFilter
{
    internal override bool IsSingleValued(JqEnvironment environment, int depth) =>
        depth < JqRecursion.MaximumDepth && left.IsSingleValued(environment, depth + 1)
        && right.IsSingleValued(environment, depth + 1);

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Value operators distribute with the left operand inner (fast),
        // matching reversed call prelude order with backtracking. Boolean
        // and/or short-circuit per left value: a decided left never touches
        // the right side, while an undecided left streams it as booleans.
        if (op is "and")
        {
            await foreach (JsonNode? l in left.Evaluate(input, context, environment).ConfigureAwait(false))
            {
                if (!Truthy(l))
                {
                    yield return JsonValue.Create(false);
                    continue;
                }
                await foreach (JsonNode? r in right.Evaluate(input, context, environment).ConfigureAwait(false))
                    yield return JsonValue.Create(Truthy(r));
            }
            yield break;
        }
        if (op is "or")
        {
            await foreach (JsonNode? l in left.Evaluate(input, context, environment).ConfigureAwait(false))
            {
                if (Truthy(l))
                {
                    yield return JsonValue.Create(true);
                    continue;
                }
                await foreach (JsonNode? r in right.Evaluate(input, context, environment).ConfigureAwait(false))
                    yield return JsonValue.Create(Truthy(r));
            }
            yield break;
        }
        else
        {
            await foreach (var r in right.Evaluate(input, context, environment).ConfigureAwait(false))
                await foreach (var l in left.Evaluate(input, context, environment).ConfigureAwait(false))
                    yield return Apply(context, op, l, r);
        }
    }

    internal static JsonNode? Apply(JqContext context, string op, JsonNode? l, JsonNode? r)
    {
        context.Budget.CheckCancellation();
        return op switch
        {
            "+" => context.Runtime.Add(l, r),
            "-" => context.Runtime.Subtract(l, r),
            "*" => context.Runtime.Multiply(l, r),
            "/" => Divide(l, r),
            "%" => Modulo(l, r),
            "==" => JsonValue.Create(context.Runtime.JsonEquals(l, r)),
            "!=" => JsonValue.Create(!context.Runtime.JsonEquals(l, r)),
            "<" => JsonValue.Create(context.Runtime.Compare(l, r) < 0),
            "<=" => JsonValue.Create(context.Runtime.Compare(l, r) <= 0),
            ">" => JsonValue.Create(context.Runtime.Compare(l, r) > 0),
            ">=" => JsonValue.Create(context.Runtime.Compare(l, r) >= 0),
            _ => throw new JqException($"unsupported operator {op}")
        };

        JsonNode? Divide(JsonNode? l, JsonNode? r)
        {
            if (TypeName(l) == "number" && TypeName(r) == "number")
            {
                double divisor = Number(r);
                if (divisor == 0.0)
                    throw new JqRuntimeException(context.Runtime.TypeError(l, r, "cannot be divided because the divisor is zero"));
                return JsonValue.Create(Number(l) / divisor);
            }
            if (TryGetString(l, out _) && TryGetString(r, out _))
                return context.Runtime.Split(l, r);
            throw new JqRuntimeException(context.Runtime.TypeError(l, r, "cannot be divided"));
        }

        // Integer remainder matching the reference: operands truncate
        // toward zero with clamping, NaN propagates, zero divisors fail.
        JsonNode? Modulo(JsonNode? l, JsonNode? r)
        {
            if (TypeName(l) != "number" || TypeName(r) != "number")
                throw new JqRuntimeException(context.Runtime.TypeError(l, r, "cannot be divided (remainder)"));
            double left = Number(l);
            double right = Number(r);
            if (double.IsNaN(left) || double.IsNaN(right))
                return JsonValue.Create(double.NaN);
            long divisor = Truncate(right);
            if (divisor == 0)
                throw new JqRuntimeException(context.Runtime.TypeError(l, r, "cannot be divided (remainder) because the divisor is zero"));
            if (divisor == -1)
                return JsonValue.Create(0L);
            return JsonValue.Create(Truncate(left) % divisor);
        }

        static long Truncate(double value)
        {
            const double MinAsDouble = -9223372036854775808.0;
            if (value < MinAsDouble)
                return long.MinValue;
            if (-value <= MinAsDouble)
                return long.MaxValue;
            return (long)value;
        }
    }
}

// Postfix `?` on any term: catchable evaluation failures yield nothing.
// Quota, compile, cancellation, and host failures still propagate.
internal sealed class OptionalFilter(JqFilter inner) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        var results = inner.Evaluate(input, context, environment).GetAsyncEnumerator();
        await using var resultsLifetime = results.ConfigureAwait(false);
        while (true)
        {
            bool moved;
            try
            {
                moved = await results.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (JqErrors.IsCatchable(exception))
            {
                yield break;
            }
            if (!moved)
                yield break;
            yield return results.Current;
        }
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        var results = inner.EvaluatePaths(pair, context, environment).GetAsyncEnumerator();
        await using var resultsLifetime = results.ConfigureAwait(false);
        while (true)
        {
            bool moved;
            try
            {
                moved = await results.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (JqErrors.IsCatchable(exception))
            {
                yield break;
            }
            if (!moved)
                yield break;
            yield return results.Current;
        }
    }

}

// Defined-or: non-false, non-null left outputs pass through; the right side
// runs only when no such output exists.
internal sealed class AlternativeFilter(JqFilter left, JqFilter right) : JqFilter
{
    internal JqFilter Left => left;
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        bool found = false;
        await foreach (JsonNode? value in left.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (Truthy(value))
            {
                found = true;
                yield return value;
            }
        }
        if (!found)
            await foreach (JsonNode? value in right.Evaluate(input, context, environment).ConfigureAwait(false))
                yield return value;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        bool found = false;
        await foreach (JqValuePath left in left.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
        {
            if (Truthy(left.Value))
            {
                found = true;
                yield return left;
            }
        }
        if (!found)
            await foreach (JqValuePath right in right.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
                yield return right;
    }

    internal JqFilter Right => right;

    internal AlternativeFilter WithRight(JqFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new AlternativeFilter(left, next);
    }
}
internal sealed class UnaryFilter(string op, JqFilter inner) : JqFilter
{
    internal override bool IsSingleValued(JqEnvironment environment, int depth) =>
        depth < JqRecursion.MaximumDepth && inner.IsSingleValued(environment, depth + 1);
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var value in inner.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (op == "-")
            {
                if (TypeName(value) != "number")
                    throw new JqRuntimeException(context.Runtime.UnaryTypeError(value, "cannot be negated"));
                yield return JsonValue.Create(-Number(value));
            }
            else if (op == "not")
            {
                yield return JsonValue.Create(!Truthy(value));
            }
            else
            {
                throw new JqException("unsupported unary operator " + op);
            }
        }
    }
}

internal sealed class IfFilter(
    IReadOnlyList<(JqFilter Condition, JqFilter Then)> branches,
    JqFilter otherwise) : JqFilter
{
    internal override bool IsSingleValued(JqEnvironment environment, int depth) =>
        depth < JqRecursion.MaximumDepth && otherwise.IsSingleValued(environment, depth + 1)
        && branches.All(branch => branch.Condition.IsSingleValued(environment, depth + 1)
            && branch.Then.IsSingleValued(environment, depth + 1));

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Reference distribution: every condition output routes independently,
        // truthy outputs run the branch body while falsy outputs fall through
        // to the rest of the chain; an empty condition yields nothing at all.
        await foreach (var value in RunBranch(0).ConfigureAwait(false))
            yield return value;

        async IAsyncEnumerable<JsonNode?> RunBranch(int index)
        {
            if (index >= branches.Count)
            {
                await foreach (var value in otherwise.Evaluate(input, context, environment).ConfigureAwait(false))
                    yield return value;
                yield break;
            }
            var (condition, then) = branches[index];
            await foreach (JsonNode? probe in condition.Evaluate(input, context, environment).ConfigureAwait(false))
            {
                if (Truthy(probe))
                {
                    await foreach (var value in then.Evaluate(input, context, environment).ConfigureAwait(false))
                        yield return value;
                }
                else
                {
                    await foreach (var value in RunBranch(index + 1).ConfigureAwait(false))
                        yield return value;
                }
            }
        }
    }

    // Rewrites tail positions (branch bodies and the final else) while
    // leaving conditions untouched.
    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValuePath next in RunBranchPaths(0).ConfigureAwait(false))
            yield return next;

        async IAsyncEnumerable<JqValuePath> RunBranchPaths(int index)
        {
            if (index >= branches.Count)
            {
                await foreach (JqValuePath next in otherwise.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
                    yield return next;
                yield break;
            }
            var (condition, then) = branches[index];
            await foreach (JsonNode? probe in condition.Evaluate(pair.Value, context, environment).ConfigureAwait(false))
            {
                if (Truthy(probe))
                {
                    await foreach (JqValuePath next in then.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
                        yield return next;
                }
                else
                {
                    await foreach (JqValuePath next in RunBranchPaths(index + 1).ConfigureAwait(false))
                        yield return next;
                }
            }
        }
    }

    internal IfFilter WithTails(JqEnvironment environment, Func<JqFilter, JqFilter> rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        if (!branches.All(branch => branch.Condition.IsSingleValued(environment, 0))) return this;
        var resolved = new List<(JqFilter Condition, JqFilter Then)>();
        foreach (var (condition, then) in branches)
            resolved.Add((condition, rewrite(then)));
        return new IfFilter(resolved, rewrite(otherwise));
    }
}

internal sealed class FunctionFilter(string name, IReadOnlyList<JqFilter> args) : JqFilter
{
    // Other operations in this dispatcher yield at most once per argument tuple.
    internal override bool IsSingleValued(JqEnvironment environment, int depth) =>
        depth < JqRecursion.MaximumDepth && name != "inputs"
        && args.All(arg => arg.IsSingleValued(environment, depth + 1));

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (name == "empty")
            yield break;

        if (name == "select")
        {
            // Predicates can yield multiple results; consume them without buffering.
            await foreach (var value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                if (Truthy(value))
                {
                    // Selection passes the input through like path-mode select
                    // and upstream DUP. Downstream updates clone their spines;
                    // retain the existing reference without cloning here.
                    yield return input;
                }
            yield break;
        }

        if (name == "error")
        {
            // Bare `error` raises the input; otherwise the first argument
            // value wins and the rest never runs. An empty argument stream
            // yields nothing at all.
            if (args.Count == 0)
                throw new JqErrorException(input);
            await foreach (var value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                throw new JqErrorException(value);
            yield break;
        }

        if (name == "halt")
            throw new JqHaltException(0, null);

        if (name == "halt_error")
        {
            // Bare `halt_error` exits 5 with the input rendered; otherwise
            // the first code value wins and the rest never runs.
            if (args.Count == 0)
                throw NewHalt(context, input, 5);
            await foreach (var value in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
                throw NewHalt(context, input, HaltCode(value));
            yield break;
        }

        if (args.Count == 0)
        {
            await foreach (JsonNode? output in EvaluateWith(Array.Empty<JsonNode?>()).ConfigureAwait(false))
                yield return output;
            yield break;
        }

        // Cartesian argument streams: the last argument is outer (slow) and
        // the first argument inner (fast), matching reversed call prelude
        // order with backtracking. An empty argument yields no outputs.
        await foreach (var combo in ArgumentCombos().ConfigureAwait(false))
            await foreach (var output in EvaluateWith(combo).ConfigureAwait(false))
                yield return output;

        yield break;

        IAsyncEnumerable<JsonNode?[]> ArgumentCombos()
        {
            var current = new JsonNode?[args.Count];
            return Combine(args.Count - 1);

            async IAsyncEnumerable<JsonNode?[]> Combine(int index)
            {
                if (index < 0)
                {
                    // EvaluateWith finishes before this vector is advanced.
                    yield return current;
                    yield break;
                }
                await foreach (var value in args[index].Evaluate(input, context, environment).ConfigureAwait(false))
                {
                    current[index] = value;
                    await foreach (var combo in Combine(index - 1).ConfigureAwait(false))
                        yield return combo;
                }
            }
        }

        static int HaltCode(JsonNode? value)
        {
            if (value is JsonValue scalar && TryGetString(scalar, out _))
                throw new JqException("halt_error requires a numeric exit code");
            return (int)Number(value);
        }

        static JqHaltException NewHalt(JqContext context, JsonNode? input, int code)
        {
            // Strings render raw with no prefix or newline; other values
            // render as JSON with a newline; null renders nothing.
            string? text = input is null ? null
                : TryGetString(input, out string? raw) ? raw
                : context.Runtime.Serialize(input, false, null, false) + "\n";
            return new JqHaltException(code, text);
        }

        // Like the reference indices definition, only array inputs and
        // string haystacks with string needles use substring search; every
        // other combination falls through to a plain .[$i] index read.
        static bool UsesSubstringSearch(JsonNode? input, JsonNode? needle) =>
            input is JsonArray || (TryGetString(input, out _) && TryGetString(needle, out _));

        async IAsyncEnumerable<JsonNode?> EvaluateWith(JsonNode?[] combo)
        {
            JsonNode? Arg(int i) => combo[i];
            if (JqMath.TryEvaluate(context, name, combo, input, out JsonNode? math))
            {
                yield return math;
                yield break;
            }

            switch (name)
            {
                case "length": yield return context.Runtime.Length(input); break;
                case "type": yield return JsonValue.Create(TypeName(input)); break;
                case "not": yield return JsonValue.Create(!Truthy(input)); break;
                case "now": yield return JsonValue.Create(JqTime.Now(context)); break;
                case "env": yield return context.Runtime.Clone(context.Variables.TryGetValue("ENV", out JsonNode? envValue) ? envValue : new JsonObject()); break;
                case "stderr":
                    if (TryGetString(input, out string? message) && message is not null)
                        await context.EmitStderrAsync(message).ConfigureAwait(false);
                    else
                    {
                        using var bytes = context.Runtime.SerializeUtf8(input, false, null, false);
                        await context.EmitStderrAsync(bytes.Value).ConfigureAwait(false);
                    }
                    yield return input;
                    break;
                case "tonumber": yield return context.Runtime.ToJsonNumber(input); break;
                case "toboolean": yield return TypeName(input) == "boolean" ? input : JsonValue.Create(context.Runtime.ToJsonBoolean(input)); break;
                case "utf8bytelength": yield return JsonValue.Create(Utf8ByteLength(input, context)); break;
                case "tostring": yield return TryGetString(input, out _) ? input : JsonValue.Create(context.Runtime.ToJqString(input)); break;
                case "tojson": yield return JsonValue.Create(context.Runtime.Serialize(input, false, null, false)); break;
                case "fromjson": yield return context.Runtime.ParseJson(input); break;
                case "min": yield return context.Runtime.MinMax(input, false); break;
                case "max": yield return context.Runtime.MinMax(input, true); break;
                case "reverse": yield return context.Runtime.Reverse(input); break;
                case "sort": yield return context.Runtime.SortArray(input); break;
                case "unique": yield return context.Runtime.UniqueArray(input); break;
                case "keys": yield return context.Runtime.Keys(input, true); break;
                case "keys_unsorted": yield return context.Runtime.Keys(input, false); break;
                case "to_entries": yield return context.Runtime.ToEntries(input); break;
                case "from_entries": yield return context.Runtime.FromEntries(input); break;
                case "arrays": if (TypeName(input) == "array") yield return input; break;
                case "objects": if (TypeName(input) == "object") yield return input; break;
                case "iterables": if (TypeName(input) is "array" or "object") yield return input; break;
                case "booleans": if (TypeName(input) == "boolean") yield return input; break;
                case "numbers": if (TypeName(input) == "number") yield return input; break;
                case "strings": if (TypeName(input) == "string") yield return input; break;
                case "nulls": if (input is null) yield return null; break;
                case "values": if (input is not null) yield return input; break;
                case "scalars": if (TypeName(input) is not ("array" or "object")) yield return input; break;
                case "finites": if (JqMath.Classify("isfinite", input)) yield return input; break;
                case "normals": if (JqMath.Classify("isnormal", input)) yield return input; break;
                case "_negate": if (TypeName(input) != "number") throw new JqRuntimeException(context.Runtime.UnaryTypeError(input, "cannot be negated")); yield return JsonValue.Create(-Number(input)); break;
                case "_strindices":
                    if (!TryGetString(input, out string? strHaystack) || strHaystack is null)
                        throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.TruncatedDump(input) + ") cannot be searched, as it is not a string");
                    if (!TryGetString(Arg(0), out string? strNeedle) || strNeedle is null)
                        throw new JqRuntimeException(TypeName(Arg(0)) + " (" + context.Runtime.TruncatedDump(Arg(0)) + ") is not a string");
                    yield return context.Runtime.Indices(input, Arg(0));
                    break;
                case "format":
                    if (!TryGetString(Arg(0), out string? formatName) || formatName is null)
                        throw new JqRuntimeException(TypeName(Arg(0)) + " (" + context.Runtime.TruncatedDump(Arg(0)) + ") is not a valid format");
                    yield return context.Runtime.FormatValue(formatName, input);
                    break;
                case "contains": yield return JsonValue.Create(context.Runtime.Contains(input, Arg(0))); break;
                case "inside": yield return JsonValue.Create(context.Runtime.Contains(Arg(0), input)); break;
                case "has": yield return JsonValue.Create(context.Runtime.Has(input, Arg(0))); break;
                case "indices":
                    if (UsesSubstringSearch(input, Arg(0)))
                        yield return context.Runtime.Indices(input, Arg(0));
                    else
                    {
                        _ = JqPathReads.TryIndex(input, Arg(0), false, context, out JsonNode? got);
                        yield return got;
                    }
                    break;
                case "index":
                case "rindex":
                    if (UsesSubstringSearch(input, Arg(0)))
                        yield return context.Runtime.Index(input, Arg(0), name == "rindex");
                    else
                    {
                        _ = JqPathReads.TryIndex(input, Arg(0), false, context, out JsonNode? values);
                        if (name == "rindex")
                            _ = JqPathReads.TrySlice(values, JsonValue.Create(-1), null, false, context, out values);
                        _ = JqPathReads.TryIndex(values, JsonValue.Create(0), false, context, out JsonNode? got);
                        yield return got;
                    }
                    break;
                case "startswith": yield return JsonValue.Create(StartsEndsWith(input, Arg(0), true)); break;
                case "endswith": yield return JsonValue.Create(StartsEndsWith(input, Arg(0), false)); break;
                case "ltrimstr": yield return context.Runtime.TrimAffix(input, Arg(0), true, false); break;
                case "rtrimstr": yield return context.Runtime.TrimAffix(input, Arg(0), false, true); break;
                case "trimstr": yield return context.Runtime.TrimAffix(input, Arg(0), true, true); break;
                case "trim": yield return context.Runtime.TrimSides(input, true, true); break;
                case "ltrim": yield return context.Runtime.TrimSides(input, true, false); break;
                case "rtrim": yield return context.Runtime.TrimSides(input, false, true); break;
                case "explode": yield return context.Runtime.Explode(input); break;
                case "implode": yield return context.Runtime.Implode(input); break;
                case "split":
                    if (combo.Length == 1)
                    {
                        yield return context.Runtime.Split(input, Arg(0));
                        break;
                    }
                    else
                    {
                        List<JsonNode> values = JqMatch.Split(context, input, Arg(0), Arg(1));
                        context.Budget.ReserveMetadata(64 + 24L * values.Count, 1);
                        var pieces = new JsonArray();
                        foreach (JsonNode piece in values)
                            pieces.Add(piece);
                        yield return pieces;
                        break;
                    }
                case "join": yield return context.Runtime.Join(input, Arg(0)); break;
                case "ascii_downcase": yield return JsonValue.Create(context.Runtime.AsciiCase(input, true)); break;
                case "ascii_upcase": yield return JsonValue.Create(context.Runtime.AsciiCase(input, false)); break;

                case "fromdate":
                case "fromdateiso8601": yield return JqTime.FromDateIso(context, input); break;
                case "todate":
                case "todateiso8601": yield return JsonValue.Create(JqTime.ToDateIso(context, input)); break;
                case "strptime": yield return JqTime.Strptime(context, input, Arg(0)); break;
                case "strftime": yield return JsonValue.Create(JqTime.Strftime(context, input, Arg(0), false)); break;
                case "strflocaltime": yield return JsonValue.Create(JqTime.Strftime(context, input, Arg(0), true)); break;
                case "gmtime": yield return JqTime.Gmtime(context, input); break;
                case "localtime": yield return JqTime.Localtime(context, input); break;
                case "mktime": yield return JqTime.MktimeUtc(context, input); break;
                case "builtins":
                {
                    var names = JqBuiltinRegistry.ListAll();
                    context.Budget.ReserveBytes(64 + 88L * names.Count);
                    var array = new JsonArray();
                    foreach (var entry in names)
                    {
                        context.Budget.CheckCancellation();
                        array.Add(JsonValue.Create(entry));
                    }
                    yield return array;
                    break;
                }
                case "modulemeta":
                {
                    if (!TryGetString(input, out string? moduleName) || moduleName is null)
                        throw new JqException("modulemeta input module name must be a string");
                    if (context.ModuleLoader is null)
                        throw new JqException("modulemeta not available");
                    yield return await context.ModuleLoader.GetModuleMetadataAsync(moduleName, context.Budget.CancellationToken).ConfigureAwait(false);
                    break;
                }
                case "input":
                {
                    var cursor = context.InputCursor ?? throw new JqException("input not available");
                    var (hasValue, next) = await cursor.PullAsync().ConfigureAwait(false);
                    if (hasValue)
                        yield return next;
                    else
                        throw new JqException("break");
                    break;
                }
                case "inputs":
                {
                    var cursor = context.InputCursor ?? throw new JqException("input not available");
                    while (true)
                    {
                        var (hasValue, next) = await cursor.PullAsync().ConfigureAwait(false);
                        if (!hasValue) break;
                        yield return next;
                    }
                    break;
                }
                case "input_filename": yield return context.InputFilename is string filename ? JsonValue.Create(filename) : null; break;
                case "input_line_number": yield return JsonValue.Create(context.InputLineNumber); break;
                default: throw new JqException($"unsupported function {name}");
            }
        }
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (name == "select")
        {
            // Predicates run in value mode; surviving pairs keep paths.
            await foreach (JsonNode? probe in args[0].Evaluate(pair.Value, context, environment).ConfigureAwait(false))
                if (Truthy(probe))
                    yield return pair;
            yield break;
        }
        if (name is "ltrimstr" or "rtrimstr" or "trimstr")
        {
            await foreach (JsonNode? affix in args[0].Evaluate(pair.Value, context, environment).ConfigureAwait(false))
            {
                bool left = name != "rtrimstr", right = name != "ltrimstr";
                if (!TryGetString(pair.Value, out string text) || !TryGetString(affix, out string fix))
                    throw new JqException((left ? "startswith" : "endswith") + "() requires string inputs");
                JqValuePath current = pair;
                if (left && text.StartsWith(fix, StringComparison.Ordinal))
                {
                    int width = context.Runtime.CountStringScalars(fix);
                    if (!JqPathReads.TrySlicePath(current, JsonValue.Create(width), null, false, context, out JqValuePath? sliced))
                        yield break;
                    current = sliced;
                    text = String(current.Value);
                }
                if (right && text.EndsWith(fix, StringComparison.Ordinal))
                {
                    int end = context.Runtime.CountStringScalars(text) - context.Runtime.CountStringScalars(fix);
                    if (!JqPathReads.TrySlicePath(current, null, JsonValue.Create(end), false, context, out JqValuePath? sliced))
                        yield break;
                    current = sliced;
                }
                yield return current;
            }
            yield break;
        }
        await foreach (JqValuePath next in base.EvaluatePathsCore(pair, context, environment).ConfigureAwait(false))
            yield return next;
    }
}
internal sealed class FormatFilter(string format) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        yield return context.Runtime.FormatValue(format, input);
    }

}

internal sealed class InterpolatedStringFilter : JqFilter
{

    private abstract record Segment;

    private sealed record Literal(string Text) : Segment;

    private sealed record Interpolation(JqFilter Filter) : Segment;

    private readonly IReadOnlyList<Segment> _segments;
    private readonly string? _format;

    // Splits once at construction so malformed templates fail before evaluation;
    // each interpolation is parsed once with the definition-site scopes, so unknown
    // names fail at compile time (stage 3) instead of per evaluation as stage 5.
    internal InterpolatedStringFilter(string template, string? format, JqSourceSpan span, JqProgramSource program, Func<string, JqFilter> parsePiece, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(parsePiece);
        _format = format;
        _segments = SplitTemplate(template, span, program, parsePiece, budget);
    }

    private static IReadOnlyList<Segment> SplitTemplate(string template, JqSourceSpan span, JqProgramSource program, Func<string, JqFilter> parsePiece, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(parsePiece);
        budget.ReserveMetadata(64, 1);
        var segments = new List<Segment>();
        int literalStart = 0;
        for (int i = 0; i < template.Length; i++)
        {
            budget.CheckCancellation();
            if (template[i] != '\\' || i + 1 == template.Length) continue;
            if (template[i + 1] == '\\') { i++; continue; }
            if (template[i + 1] != '(') continue;
            AddLiteral(literalStart, i);
            int start = i + 2;
            i = Lexer.FindInterpolationEnd(template, start, span, program, budget);
            if (i == template.Length)
                throw new JqCompileException("unterminated string interpolation", span, program);
            budget.ReserveString(i - start);
            budget.ReserveMetadata(64, 1);
            segments.Add(new Interpolation(parsePiece(template[start..i])));
            literalStart = i + 1;
        }
        AddLiteral(literalStart, template.Length);
        return segments;

        void AddLiteral(int start, int end)
        {
            if (start == end) return;
            budget.ReserveString(end - start);
            budget.ReserveMetadata(64, 1);
            segments.Add(new Literal(Lexer.DecodeLiterals(template[start..end], budget)));
        }
    }

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (_segments.Count == 0) { yield return JsonValue.Create(string.Empty); yield break; }
        context.Budget.ReserveBytes(64L * _segments.Count);
        var iterators = new IAsyncEnumerator<string>?[_segments.Count];
        var pieces = new string?[_segments.Count];
        int index = _segments.Count - 1;
        try
        {
            while (index < _segments.Count)
            {
                context.Budget.CheckCancellation();
                var iterator = iterators[index] ??= Pieces(_segments[index]).GetAsyncEnumerator();
                if (!await iterator.MoveNextAsync().ConfigureAwait(false))
                {
                    await iterator.DisposeAsync().ConfigureAwait(false);
                    iterators[index] = null;
                    pieces[index] = null;
                    index++;
                    continue;
                }
                pieces[index] = iterator.Current;
                if (index != 0) { index--; continue; }
                var assembled = new StringBuilder();
                foreach (var piece in pieces)
                    context.Budget.Append(assembled, piece.AsSpan());
                yield return JsonValue.Create(context.Budget.Finish(assembled));
            }
        }
        finally
        {
            // Earlier occurrences are inner (fast); unwind in that order.
            foreach (var iterator in iterators)
                if (iterator is not null) await iterator.DisposeAsync().ConfigureAwait(false);
        }

        async IAsyncEnumerable<string> Pieces(Segment segment)
        {
            if (segment is Literal literal) { yield return literal.Text; yield break; }
            if (segment is Interpolation interpolation)
            {
                if (interpolation.Filter is SingleValueFilter)
                {
                    yield return Render(await interpolation.Filter.Evaluate(input, context, environment).SingleAsync().ConfigureAwait(false));
                    yield break;
                }
                await foreach (var value in interpolation.Filter.Evaluate(input, context, environment).ConfigureAwait(false))
                    yield return Render(value);
            }
        }

        string Render(JsonNode? value) => _format is null
            ? context.Runtime.ToJqString(value)
            : context.Runtime.Format(_format, value);
    }
}
