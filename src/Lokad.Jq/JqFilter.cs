using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Lokad.Jq.Helpers;
using static Lokad.Jq.JqRuntime;
using static Lokad.Jq.JqPaths;

namespace Lokad.Jq;

internal abstract class JqFilter
{
    public virtual async IAsyncEnumerable<JqValue?> Evaluate(JqValue? input, JqContext context, JqEnvironment environment)
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
    protected abstract IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment);

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

    // Each yielded container is constructed here and has not escaped through
    // bindings or another result. Collectors may take it without another copy.
    internal virtual bool ConstructsFreshContainer => false;

    internal static JqValuePath TrackResult(JqValuePath pair, JqValue? value)
    {
        JqValuePath? origin = pair.Tracked ? pair : pair.Origin;
        bool intact = origin is not null && PathIntact(origin.Value, value);
        return intact && origin is not null ? origin.WithValue(value, true) : pair.WithValue(value, false);
    }

    protected virtual async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValue? value in Evaluate(pair.Value, context, environment).ConfigureAwait(false))
            yield return TrackResult(pair, value);
    }

    // Heap values retain their identity by reference. Immediate numbers keep
    // their representation, including signed zero and NaN bits.
    internal static bool PathIntact(JqValue? expected, JqValue? actual)
    {
        if (ReferenceEquals(expected, actual)) return true;
        if (expected is JqScalar a && actual is JqScalar b
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
    public sealed override IAsyncEnumerable<JqValue?> Evaluate(JqValue? input, JqContext context, JqEnvironment environment) =>
        new ValueCursor(this, input, context, environment);

    internal JqValue? ReadValue(JqValue? input, JqContext context, JqEnvironment environment)
    {
        context.Budget.EnterRecursion();
        try
        {
            JqValue? value = EvaluateValue(input, context, environment);
            context.Budget.CheckCancellation();
            return value;
        }
        finally { context.Budget.LeaveRecursion(); }
    }

    private sealed class ValueCursor(SingleValueFilter filter, JqValue? input, JqContext context, JqEnvironment environment)
        : IAsyncEnumerable<JqValue?>, IAsyncEnumerator<JqValue?>
    {
        private int _enumerated;
        private bool _started;
        public JqValue? Current { get; private set; }

        public IAsyncEnumerator<JqValue?> GetAsyncEnumerator(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) == 0) return this;
            return new ValueCursor(filter, input, context, environment) { _enumerated = 1 };
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_started) { Current = null; return ValueTask.FromResult(false); }
            _started = true;
            Current = filter.ReadValue(input, context, environment);
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            _started = true;
            Current = null;
            return default;
        }
    }

    protected sealed override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        yield return EvaluateValue(input, context, environment);
    }

    protected abstract JqValue? EvaluateValue(JqValue? input, JqContext context, JqEnvironment environment);
}

internal sealed class IdentityFilter : SingleValueFilter
{
    protected override JqValue? EvaluateValue(JqValue? input, JqContext context, JqEnvironment environment)
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

internal sealed class LiteralFilter(JqValue? value) : SingleValueFilter
{
    internal JqValue? Value => value;

    protected override JqValue? EvaluateValue(JqValue? input, JqContext context, JqEnvironment environment)
    {
        return value;
    }
}

internal sealed class VariableFilter(string name) : SingleValueFilter
{
    protected override JqValue? EvaluateValue(JqValue? input, JqContext context, JqEnvironment environment)
    {
        if (!environment.TryGetValue(name, out JqValue? value))
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
        if (!environment.TryGetValue(name, out JqValue? value))
            throw new JqException($"undefined variable ${name}");
        yield return TrackResult(pair, value);
    }
}

// Source location: a parse-time constant capturing the file and line where
// `$__loc__` occurs. The file is the module display path or `<top-level>`
// for inline filters; the line is 1-based.
internal sealed class LocFilter(string file, int line) : JqFilter
{
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        var location = new JqObject(context.Budget) {
            ["file"] = JqScalar.Create(file, context.Budget),
            ["line"] = JqScalar.Create(line),
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
    internal override bool ConstructsFreshContainer => body.ConstructsFreshContainer;
    private readonly HashSet<string> _allNames = CollectAll(alternatives);
    private JqFilter Source => source;
    private IReadOnlyList<BindingPattern> Alternatives => alternatives;
    private JqFilter Body => body;

    internal static HashSet<string> CollectAll(IReadOnlyList<BindingPattern> alternatives)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (BindingPattern alternative in alternatives)
            alternative.CollectBoundNames(names);
        return names;
    }

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        if (source is SingleValueFilter && alternatives is [VariablePattern])
        {
            // A pure source produces exactly one binding. Batch a contiguous
            // prefix rather than keeping an As/Match/alternative iterator per
            // binding around every output of the eventual asynchronous body.
            using var storage = new JqScopeStorage(context.Budget);
            JqEnvironment scope = environment;
            JqFilter continuation = this;
            while (continuation is AsFilter { Source: SingleValueFilter single, Alternatives: [VariablePattern variable] } binding)
            {
                JqValue? value = single.ReadValue(input, context, scope);
                storage.Reserve(128, 1);
                scope = scope.Extend(variable.Name, value);
                continuation = binding.Body;
            }
            await foreach (JqValue? output in continuation.Evaluate(input, context, scope).ConfigureAwait(false))
                yield return output;
            yield break;
        }
        await foreach (JqValue? bound in source.Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (var (_, output) in DriveAlternatives(alternatives, _allNames, bound, environment, context, scope => body.Evaluate(input, context, scope)).ConfigureAwait(false))
                yield return output;
    }

    // Shared alternative driver for bindings and reductions: every name
    // starts null, the first non-erroring alternative wins (even when it
    // yields nothing), and catchable match or body errors retry later
    // alternatives. Reduction drivers reuse it with collecting bodies.
    internal static async IAsyncEnumerable<(JqEnvironment Scope, JqValue? Output)> DriveAlternatives(IReadOnlyList<BindingPattern> alternatives, HashSet<string> allNames, JqValue? bound, JqEnvironment environment, JqContext context, Func<JqEnvironment, IAsyncEnumerable<JqValue?>> body)
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
            var scopes = Match(alternatives[index], bound, prebound, environment, context, storage, 1).GetAsyncEnumerator();
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

    private static async IAsyncEnumerable<JqEnvironment> Match(BindingPattern pattern, JqValue? value, JqEnvironment scope, JqEnvironment keyScope, JqContext context, JqScopeStorage storage, int depth)
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
                    await foreach (JqEnvironment inner in Match(alias.Inner, value, aliased, keyScope, context, storage, depth + 1).ConfigureAwait(false))
                        yield return inner;
                }
                finally { storage.Release(128); }
                break;
            case ArrayPattern array:
                await foreach (JqEnvironment bound in (MatchSequence(array.Items.Count, scope, context, storage,
                    (index, current) => Match(array.Items[index], JqPathReads.ReadIndex(value, index, false, context), current, keyScope, context, storage, depth + 1))).ConfigureAwait(false))
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
            // Key expressions are compiled in the enclosing lexical scope.
            // Provisional nulls and earlier bindings in this pattern apply to
            // the body, not to keys in the same destructuring operation.
            await foreach (JqValue? keyValue in property.Key.Evaluate(value, context, keyScope).ConfigureAwait(false))
            {
                if (keyValue is not JqString key)
                    throw new JqException($"Cannot use {TypeName(keyValue)} ({context.Runtime.TruncatedDump(keyValue)}) as object key");
                JqValue? field = JqPathReads.ReadField(value, key, context.Runtime, context.Budget);
                await foreach (JqEnvironment bound in Match(property.Value, field, current, keyScope, context, storage, depth + 1).ConfigureAwait(false))
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
            JqValue? bound = sourcePair.Value;
            bool completed = false;
            for (int index = 0; index < alternatives.Count && !completed; index++)
            {
                bool isLast = index == alternatives.Count - 1;
                var results = RunPathAlternative(alternatives[index], bound, prebound, environment, pair, context, storage).GetAsyncEnumerator();
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

    private async IAsyncEnumerable<JqValuePath> RunPathAlternative(BindingPattern alternative, JqValue? bound, JqEnvironment prebound, JqEnvironment keyScope, JqValuePath pair, JqContext context, JqScopeStorage storage)
    {
        await foreach (JqEnvironment scope in Match(alternative, bound, prebound, keyScope, context, storage, 1).ConfigureAwait(false))
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
    protected override IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment) =>
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

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetFunctionQualified(Name, Arity, out JqUserClosure? closure) || closure is null)
            throw new JqException($"undefined function {Name}/{Arity}");
        if (Args.Count != closure.Definition.Arity)
            throw new JqException($"undefined function {Name}/{Arity}");
        await foreach (JqValue? value in EvaluateCallLoop(closure, input, Args, environment, context).ConfigureAwait(false))
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

    private sealed record CallFrame(JqValue? Input, JqValue?[] Values, IReadOnlyList<int> Positions,
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

    internal static IAsyncEnumerable<JqValue?> EvaluateCallLoop(JqUserClosure closure, JqValue? input,
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
                await foreach (JqValue?[] combo in Combine(source.Value).ConfigureAwait(false))
                    yield return new CallFrame(source.Value, combo, positions, filters, source.Path);

            async IAsyncEnumerable<(JqValue? Value, JqValuePath? Path)> Sources()
            {
                if (next.InputSource is not JqFilter source)
                    yield return (next.Input, next.Path);
                else if (next.Path is JqValuePath path)
                    await foreach (JqValuePath value in source.EvaluatePaths(path, context, next.Environment).ConfigureAwait(false))
                        yield return (value.Value, value);
                else
                    await foreach (JqValue? value in source.Evaluate(next.Input, context, next.Environment).ConfigureAwait(false))
                        yield return (value, null);
            }

            // First argument varies slowest. One reusable vector and iterative
            // cursors avoid both a cartesian inventory and an arity-sized C# stack.
            async IAsyncEnumerable<JqValue?[]> Combine(JqValue? input)
            {
                var currentValues = new JqValue?[values.Count];
                if (values.Count == 0)
                {
                    yield return currentValues;
                    yield break;
                }
                var iterators = new IAsyncEnumerator<JqValue?>?[values.Count];
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
                            IAsyncEnumerator<JqValue?> iterator = iterators[index] ??=
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
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JqFilterClosure closure = Resolve(environment);
        await foreach (JqValue? value in closure.Filter.Evaluate(input, context, closure.Environment).ConfigureAwait(false))
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
internal sealed class TailCallSignal(JqFunctionDefinition definition, JqValue? input, JqEnvironment environment,
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
    internal JqValue? Input { get; } = input;
    internal JqEnvironment Environment { get; } = environment;
    internal IReadOnlyList<JqFilter> Args { get; } = args;
    internal JqFilter? InputSource { get; } = source;
    internal JqValuePath? Path { get; } = path;
}

internal sealed class TailSelfCallFilter(JqFunctionDefinition Definition, IReadOnlyList<JqFilter> Args) : JqFilter
{
    protected override IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment) =>
        throw new TailCallSignal(Definition, input, environment, Args, null, null);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        throw new TailCallSignal(Definition, pair.Value, environment, Args, null, pair);

}

internal sealed class PipeFilter(JqFilter left, JqFilter right) : JqFilter
{
    internal override bool ConstructsFreshContainer => right.ConstructsFreshContainer;
    internal override bool IsSingleValued(JqEnvironment environment, int depth) =>
        depth < JqRecursion.MaximumDepth && left.IsSingleValued(environment, depth + 1)
        && right.IsSingleValued(environment, depth + 1);
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        // Pure single-value leaves already expose guarded synchronous reads.
        // Use those inside this same async pipeline instead of allocating a
        // one-element cursor for every record. Host-capable filters still await.
        if (left is SingleValueFilter first)
        {
            JqValue? value = first.ReadValue(input, context, environment);
            if (right is SingleValueFilter last)
                yield return last.ReadValue(value, context, environment);
            else
                await foreach (var output in right.Evaluate(value, context, environment).ConfigureAwait(false))
                    yield return output;
            yield break;
        }
        if (right is SingleValueFilter single)
        {
            await foreach (var value in left.Evaluate(input, context, environment).ConfigureAwait(false))
                yield return single.ReadValue(value, context, environment);
            yield break;
        }
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
    protected override IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment) =>
        throw new TailCallSignal(Definition, input, environment, Args, Source, null);

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        throw new TailCallSignal(Definition, pair.Value, environment, Args, Source, pair);
}

internal sealed class CommaFilter(JqFilter left, JqFilter right) : JqFilter
{
    internal JqFilter Left => left;
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
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

internal sealed class FieldFilter(JqFilter source, JqString name, bool optional) : JqFilter
{
    internal static JqFilter Create(JqFilter source, JqString name, bool optional) =>
        !optional && source is SingleValueFilter scalar ? new ValueFilter(scalar, name) : new FieldFilter(source, name, optional);

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var value in source.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (optional && value is not null && value is not JqObject) continue;
            yield return JqPathReads.ReadField(value, name, context.Runtime, context.Budget);
        }
    }

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
        ReadPaths(source, name, optional, pair, context, environment);

    private static async IAsyncEnumerable<JqValuePath> ReadPaths(JqFilter filter, JqString name, bool optional,
        JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValuePath source in filter.EvaluatePaths(pair, context, environment).ConfigureAwait(false))
        {
            RequireTracked(source, new KeySegment(name), context);
            if (optional && source.Value is not null && source.Value is not JqObject) continue;
            JqValue? field = JqPathReads.ReadField(source.Value, name, context.Runtime, context.Budget);
            yield return new JqValuePath(Extend(source.Segments, new KeySegment(name), context.Budget), field, true);
        }
    }

    // A required field of a single-result source returns one value or fails.
    // Reuse the scalar cursor instead of suspending a streaming source iterator.
    private sealed class ValueFilter(SingleValueFilter source, JqString name) : SingleValueFilter
    {
        protected override JqValue? EvaluateValue(JqValue? input, JqContext context, JqEnvironment environment) =>
            JqPathReads.ReadField(source.ReadValue(input, context, environment), name, context.Runtime, context.Budget);

        protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment) =>
            ReadPaths(source, name, false, pair, context, environment);
    }
}

internal sealed class IndexFilter(JqFilter source, JqFilter index, bool optional) : JqFilter
{
    internal static JqFilter Create(JqFilter source, JqFilter index, bool optional) =>
        !optional && source is SingleValueFilter value && index is SingleValueFilter key
            ? new ValueFilter(value, key) : new IndexFilter(source, index, optional);

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValue? key in index.Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JqValue? value in source.Evaluate(input, context, environment).ConfigureAwait(false))
                if (JqPathReads.TryIndex(value, key, optional, context, out JqValue? result))
                    yield return result;
    }

    protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment) =>
        ReadPaths(source, index, optional, outer, context, environment);

    private static async IAsyncEnumerable<JqValuePath> ReadPaths(JqFilter source, JqFilter index, bool optional,
        JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        // Keys are subexpressions of the enclosing input, before the source.
        // Optional access suppresses read failures, never key/source effects.
        await foreach (JqValue? key in index.Evaluate(outer.Value, context, environment).ConfigureAwait(false))
            await foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment).ConfigureAwait(false))
            {
                if (!pair.Tracked) RequireTracked(pair, PathSegmentFor(key, context), context);
                if (JqPathReads.TryIndex(pair.Value, key, optional, context, out JqValue? result))
                    yield return new JqValuePath(Extend(pair.Segments, PathSegmentFor(key, context), context.Budget), result, true);
            }
    }

    private static JqValueSegment PathSegmentFor(JqValue? key, JqContext context)
    {
        if (key is JqString name) return new KeySegment(name);
        if (JqPaths.TryGetFractional(key, out double fractional)) return new FractionalSegment(fractional);
        if (TryGetIndex(key, out long index, out bool isNaN)) return new IndexSegment(index, isNaN);
        return new InvalidSegment(context.Budget.CopyValue(key, 0));
    }

    private sealed class ValueFilter(SingleValueFilter source, SingleValueFilter index) : SingleValueFilter
    {
        protected override JqValue? EvaluateValue(JqValue? input, JqContext context, JqEnvironment environment)
        {
            JqValue? key = index.ReadValue(input, context, environment);
            JqValue? value = source.ReadValue(input, context, environment);
            // Required indexing returns one value (including null) or throws.
            _ = JqPathReads.TryIndex(value, key, false, context, out JqValue? result);
            return result;
        }

        protected override IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment) =>
            ReadPaths(source, index, false, outer, context, environment);
    }
}

internal sealed class SliceFilter(JqFilter source, JqFilter? start, JqFilter? end, bool optional) : JqFilter
{
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValue? lower in Bounds(start, input, context, environment).ConfigureAwait(false))
            await foreach (JqValue? upper in Bounds(end, input, context, environment).ConfigureAwait(false))
                await foreach (JqValue? value in source.Evaluate(input, context, environment).ConfigureAwait(false))
                    if (JqPathReads.TrySlice(value, lower, upper, optional, context, out JqValue? result))
                        yield return result;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValue? lower in Bounds(start, outer.Value, context, environment).ConfigureAwait(false))
            await foreach (JqValue? upper in Bounds(end, outer.Value, context, environment).ConfigureAwait(false))
                await foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment).ConfigureAwait(false))
                {
                    if (JqPathReads.TrySlicePath(pair, lower, upper, optional, context, out JqValuePath? result))
                        yield return result;
                }
    }

    private static async IAsyncEnumerable<JqValue?> Bounds(JqFilter? bound, JqValue? input, JqContext context, JqEnvironment environment)
    {
        if (bound is null) yield return null;
        else
            await foreach (JqValue? value in bound.Evaluate(input, context, environment).ConfigureAwait(false))
                yield return value;
    }
}

internal sealed class IteratorFilter(JqFilter source, bool optional) : JqFilter
{
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var value in source.Evaluate(input, context, environment).ConfigureAwait(false))
            foreach (JqValue? child in ReadValues(value, optional, context.Budget))
                yield return child;
    }

    internal static IEnumerable<JqValue?> ReadValues(JqValue? value, bool optional, JqBudget budget)
    {
        budget.CheckCancellation();
        if (value is JqArray arr)
        {
            foreach (JqValue? child in arr)
            {
                budget.CheckCancellation();
                yield return child;
            }
        }
        else if (value is JqObject obj)
        {
            foreach (var child in obj)
            {
                budget.CheckCancellation();
                yield return child.Value;
            }
        }
        else if (!optional)
            throw new JqException($"cannot iterate over {TypeName(value)}");
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        await foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment).ConfigureAwait(false))
            foreach (JqValuePath child in ReadPaths(pair, context))
                yield return child;
    }

    internal static IEnumerable<JqValuePath> ReadPaths(JqValuePath pair, JqContext context)
    {
        if ((pair.Value is JqArray || pair.Value is JqObject) && !pair.Tracked)
            throw new JqException(InvalidIterate(pair.Value, context));
        if (pair.Value is JqArray arr)
        {
            for (int index = 0; index < arr.Count; index++)
                yield return new JqValuePath(Extend(pair.Segments, new IndexSegment(index, false), context.Budget), arr[index], true);
        }
        else if (pair.Value is JqObject obj)
        {
            foreach (var property in obj)
                yield return new JqValuePath(Extend(pair.Segments, new KeySegment(property.Key), context.Budget), property.Value, true);
        }
        else
        {
            if (!pair.Tracked)
                throw new JqException(InvalidIterate(pair.Value, context));
            throw new JqException($"cannot iterate over {TypeName(pair.Value)}");
        }
    }
}

// Pre-order depth-first traversal: the value itself, then each child in
// order, matching recursive descent through iteration.
internal sealed class RecursiveDescentFilter : JqFilter
{
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        yield return input;
        using var cursor = new JqTreeCursor(context.Budget);
        JqValue? current = input;
        while (true)
        {
            if (current is JqArray { Count: > 0 } or JqObject { Count: > 0 }) cursor.Push(current);
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
        JqValue? current = pair.Value;
        while (true)
        {
            if (current is JqArray { Count: > 0 } or JqObject { Count: > 0 }) cursor.Push(current);
            while (cursor.Count != 0 && !cursor.TryNext(out current)) cursor.Pop();
            if (cursor.Count == 0) yield break;
            yield return new JqValuePath(cursor.CopySegments(pair.Segments), current, true);
        }
    }
}

internal sealed class ArrayFilter(JqFilter item) : JqFilter
{
    internal JqFilter Item => item;
    internal override bool ConstructsFreshContainer => true;

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        context.Budget.ReserveBytes(64);
        var array = new JqArray();
        bool fresh = item.ConstructsFreshContainer;
        await foreach (var value in item.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            context.Budget.ReserveBytes(value is JqScalar ? 88 : 24);
            array.Add(context.Budget.CollectValue(value, 1, fresh));
        }
        yield return array;
    }
}

internal sealed record ObjectProperty(JqString? StaticKey, JqFilter? KeyFilter, JqFilter Value);

internal sealed class ObjectFilter(IReadOnlyList<ObjectProperty> properties) : JqFilter
{
    internal IReadOnlyList<ObjectProperty> Properties => properties;
    internal override bool ConstructsFreshContainer => true;

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        if (properties.Count == 0)
        {
            context.Budget.ReserveBytes(64);
            yield return new JqObject(context.Budget);
            yield break;
        }
        using var workspace = new JqScopeStorage(context.Budget);
        workspace.Reserve(128L * properties.Count, properties.Count);
        var fields = new Dictionary<JqString, JqValue?>(properties.Count, context.Budget.StringKeys);
        var iterators = new IAsyncEnumerator<(JqString Key, JqValue? Value)>?[properties.Count];
        var keys = new JqString?[properties.Count];
        var previous = new (bool Exists, JqValue? Value)[properties.Count];
        int index = 0;
        try
        {
            while (index >= 0)
            {
                context.Budget.CheckCancellation();
                ObjectProperty property = properties[index];
                if (keys[index] is { } oldKey)
                {
                    RestoreField(oldKey, previous[index]);
                    keys[index] = null;
                    // A static scalar field has no second choice. Restoring
                    // its previous binding completes this product level.
                    if (property is { StaticKey: not null, KeyFilter: null, Value: SingleValueFilter })
                    {
                        index--;
                        continue;
                    }
                }
                (JqString Key, JqValue? Value) choice;
                if (property is { StaticKey: JqString key, KeyFilter: null, Value: SingleValueFilter single })
                    choice = (key, single.ReadValue(input, context, environment));
                else
                {
                    var iterator = iterators[index] ??= Choices(property).GetAsyncEnumerator();
                    if (!await iterator.MoveNextAsync().ConfigureAwait(false))
                    {
                        await iterator.DisposeAsync().ConfigureAwait(false);
                        iterators[index] = null;
                        index--;
                        continue;
                    }
                    choice = iterator.Current;
                }
                previous[index] = (fields.TryGetValue(choice.Key, out var oldValue), oldValue);
                fields[choice.Key] = choice.Value;
                keys[index] = choice.Key;
                if (index + 1 < properties.Count) { index++; continue; }
                context.Budget.ReserveBytes(64 + 96L * fields.Count);
                context.Budget.ReserveBytes(64L * fields.Values.Count(value => value is JqScalar));
                var result = new JqObject(context.Budget);
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

        async IAsyncEnumerable<(JqString Key, JqValue? Value)> Choices(ObjectProperty property)
        {
            if (property is { StaticKey: JqString key, KeyFilter: null })
            {
                await foreach (var value in Values(property.Value).ConfigureAwait(false)) yield return (key, value);
            }
            else if (property.KeyFilter is JqFilter keyFilter)
            {
                await foreach (var keyValue in Values(keyFilter).ConfigureAwait(false))
                {
                    if (keyValue is not JqString keyName)
                        throw new JqException($"Cannot use {TypeName(keyValue)} ({context.Runtime.TruncatedDump(keyValue)}) as object key");
                    await foreach (var value in Values(property.Value).ConfigureAwait(false)) yield return (keyName, value);
                }
            }
        }

        async IAsyncEnumerable<JqValue?> Values(JqFilter filter)
        {
            // Scalar leaves need no suspended evaluator frame while later
            // properties run. Complete their single-value iterator first.
            if (filter is SingleValueFilter single)
            {
                JqValue? value = single.ReadValue(input, context, environment);
                yield return value;
                yield break;
            }
            await foreach (JqValue? value in filter.Evaluate(input, context, environment).ConfigureAwait(false))
                yield return value;
        }

        void RestoreField(JqString key, (bool Exists, JqValue? Value) old)
        {
            if (old.Exists) fields[key] = old.Value;
            else fields.Remove(key);
        }
    }
}

internal sealed class BinaryFilter(JqFilter left, string op, JqFilter right) : JqFilter
{
    internal static JqFilter Create(JqFilter left, string op, JqFilter right) =>
        left is SingleValueFilter first && right is SingleValueFilter second
            ? new ValueFilter(first, op, second) : new BinaryFilter(left, op, right);

    internal override bool IsSingleValued(JqEnvironment environment, int depth) =>
        depth < JqRecursion.MaximumDepth && left.IsSingleValued(environment, depth + 1)
        && right.IsSingleValued(environment, depth + 1);

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        // Value operators distribute with the left operand inner (fast),
        // matching reversed call prelude order with backtracking. Boolean
        // and/or short-circuit per left value: a decided left never touches
        // the right side, while an undecided left streams it as booleans.
        if (op is "and")
        {
            await foreach (JqValue? l in left.Evaluate(input, context, environment).ConfigureAwait(false))
            {
                if (!Truthy(l))
                {
                    yield return JqScalar.Create(false);
                    continue;
                }
                await foreach (JqValue? r in right.Evaluate(input, context, environment).ConfigureAwait(false))
                    yield return JqScalar.Create(Truthy(r));
            }
            yield break;
        }
        if (op is "or")
        {
            await foreach (JqValue? l in left.Evaluate(input, context, environment).ConfigureAwait(false))
            {
                if (Truthy(l))
                {
                    yield return JqScalar.Create(true);
                    continue;
                }
                await foreach (JqValue? r in right.Evaluate(input, context, environment).ConfigureAwait(false))
                    yield return JqScalar.Create(Truthy(r));
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

    private sealed class ValueFilter(SingleValueFilter left, string op, SingleValueFilter right) : SingleValueFilter
    {
        protected override JqValue? EvaluateValue(JqValue? input, JqContext context, JqEnvironment environment)
        {
            if (op is "and" or "or")
            {
                bool first = Truthy(left.ReadValue(input, context, environment));
                return JqScalar.Create(op == "and"
                    ? first && Truthy(right.ReadValue(input, context, environment))
                    : first || Truthy(right.ReadValue(input, context, environment)));
            }
            // Ordinary operators retain jq's right-before-left operand order.
            JqValue? second = right.ReadValue(input, context, environment);
            return Apply(context, op, left.ReadValue(input, context, environment), second);
        }
    }

    internal static JqValue? Apply(JqContext context, string op, JqValue? l, JqValue? r)
    {
        context.Budget.CheckCancellation();
        return op switch
        {
            "+" => context.Runtime.Add(l, r),
            "-" => context.Runtime.Subtract(l, r),
            "*" => context.Runtime.Multiply(l, r),
            "/" => Divide(l, r),
            "%" => Modulo(l, r),
            "==" => JqScalar.Create(context.Runtime.JsonEquals(l, r)),
            "!=" => JqScalar.Create(!context.Runtime.JsonEquals(l, r)),
            "<" => JqScalar.Create(context.Runtime.Compare(l, r) < 0),
            "<=" => JqScalar.Create(context.Runtime.Compare(l, r) <= 0),
            ">" => JqScalar.Create(context.Runtime.Compare(l, r) > 0),
            ">=" => JqScalar.Create(context.Runtime.Compare(l, r) >= 0),
            _ => throw new JqException($"unsupported operator {op}")
        };

        JqValue? Divide(JqValue? l, JqValue? r)
        {
            if (TypeName(l) == "number" && TypeName(r) == "number")
            {
                double divisor = Number(r);
                if (divisor == 0.0)
                    throw new JqException(context.Runtime.TypeError(l, r, "cannot be divided because the divisor is zero"));
                return JqScalar.Create(Number(l) / divisor);
            }
            if (l is JqString && r is JqString)
                return context.Runtime.Split(l, r);
            throw new JqException(context.Runtime.TypeError(l, r, "cannot be divided"));
        }

        // Integer remainder matching the reference: operands truncate
        // toward zero with clamping, NaN propagates, zero divisors fail.
        JqValue? Modulo(JqValue? l, JqValue? r)
        {
            if (TypeName(l) != "number" || TypeName(r) != "number")
                throw new JqException(context.Runtime.TypeError(l, r, "cannot be divided (remainder)"));
            double left = Number(l);
            double right = Number(r);
            if (double.IsNaN(left) || double.IsNaN(right))
                return JqScalar.Create(double.NaN);
            long divisor = Truncate(right);
            if (divisor == 0)
                throw new JqException(context.Runtime.TypeError(l, r, "cannot be divided (remainder) because the divisor is zero"));
            if (divisor == -1)
                return JqScalar.Create(0L);
            return JqScalar.Create(Truncate(left) % divisor);
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
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
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
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        bool found = false;
        await foreach (JqValue? value in left.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (Truthy(value))
            {
                found = true;
                yield return value;
            }
        }
        if (!found)
            await foreach (JqValue? value in right.Evaluate(input, context, environment).ConfigureAwait(false))
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
    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var value in inner.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (op == "-")
            {
                if (TypeName(value) != "number")
                    throw new JqException(context.Runtime.UnaryTypeError(value, "cannot be negated"));
                yield return JqScalar.Create(-Number(value));
            }
            else if (op == "not")
            {
                yield return JqScalar.Create(!Truthy(value));
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

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        // Reference distribution: every condition output routes independently,
        // truthy outputs run the branch body while falsy outputs fall through
        // to the rest of the chain; an empty condition yields nothing at all.
        await foreach (var value in RunBranch(0).ConfigureAwait(false))
            yield return value;

        async IAsyncEnumerable<JqValue?> RunBranch(int index)
        {
            if (index >= branches.Count)
            {
                await foreach (var value in otherwise.Evaluate(input, context, environment).ConfigureAwait(false))
                    yield return value;
                yield break;
            }
            var (condition, then) = branches[index];
            await foreach (JqValue? probe in condition.Evaluate(input, context, environment).ConfigureAwait(false))
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
            await foreach (JqValue? probe in condition.Evaluate(pair.Value, context, environment).ConfigureAwait(false))
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

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
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
            await foreach (JqValue? output in EvaluateWith(Array.Empty<JqValue?>()).ConfigureAwait(false))
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

        IAsyncEnumerable<JqValue?[]> ArgumentCombos()
        {
            var current = new JqValue?[args.Count];
            return Combine(args.Count - 1);

            async IAsyncEnumerable<JqValue?[]> Combine(int index)
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

        static int HaltCode(JqValue? value)
        {
            if (value is JqString)
                throw new JqException("halt_error requires a numeric exit code");
            return (int)Number(value);
        }

        static JqHaltException NewHalt(JqContext context, JqValue? input, int code)
        {
            context.Budget.CheckCancellation();
            return new JqHaltException(code, input);
        }

        // Like the reference indices definition, only array inputs and
        // string haystacks with string needles use substring search; every
        // other combination falls through to a plain .[$i] index read.
        static bool UsesSubstringSearch(JqValue? input, JqValue? needle) =>
            input is JqArray || (input is JqString && needle is JqString);

        async IAsyncEnumerable<JqValue?> EvaluateWith(JqValue?[] combo)
        {
            JqValue? Arg(int i) => combo[i];
            if (JqMath.TryEvaluate(context, name, combo, input, out JqValue? math))
            {
                yield return math;
                yield break;
            }

            switch (name)
            {
                case "length": yield return context.Runtime.Length(input); break;
                case "type": yield return JqScalar.Create(TypeName(input), context.Budget); break;
                case "not": yield return JqScalar.Create(!Truthy(input)); break;
                case "now": yield return JqScalar.Create(JqTime.Now(context)); break;
                case "env": yield return context.Runtime.Clone(context.Variables.TryGetValue("ENV", out JqValue? envValue) ? envValue : new JqObject(context.Budget)); break;
                case "stderr":
                    if (input is JqString message)
                        await context.EmitStderrAsync(message.Utf8).ConfigureAwait(false);
                    else
                    {
                        using var bytes = context.Runtime.SerializeUtf8(input, false, null, false);
                        await context.EmitStderrAsync(bytes.Value).ConfigureAwait(false);
                    }
                    yield return input;
                    break;
                case "tonumber": yield return context.Runtime.ToJsonNumber(input); break;
                case "toboolean": yield return TypeName(input) == "boolean" ? input : JqScalar.Create(context.Runtime.ToJsonBoolean(input)); break;
                case "utf8bytelength": yield return JqScalar.Create(Utf8ByteLength(input, context)); break;
                case "tostring": yield return context.Runtime.ToNativeString(input); break;
                case "tojson": yield return context.Runtime.ToJsonString(input); break;
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
                case "_negate": if (TypeName(input) != "number") throw new JqException(context.Runtime.UnaryTypeError(input, "cannot be negated")); yield return JqScalar.Create(-Number(input)); break;
                case "_strindices":
                    if (input is not JqString)
                        throw new JqException(TypeName(input) + " (" + context.Runtime.TruncatedDump(input) + ") cannot be searched, as it is not a string");
                    if (Arg(0) is not JqString)
                        throw new JqException(TypeName(Arg(0)) + " (" + context.Runtime.TruncatedDump(Arg(0)) + ") is not a string");
                    yield return context.Runtime.Indices(input, Arg(0));
                    break;
                case "format":
                    if (Arg(0) is not JqString formatName)
                        throw new JqException(TypeName(Arg(0)) + " (" + context.Runtime.TruncatedDump(Arg(0)) + ") is not a valid format");
                    yield return context.Runtime.FormatValue(formatName, input);
                    break;
                case "contains": yield return JqScalar.Create(context.Runtime.Contains(input, Arg(0))); break;
                case "inside": yield return JqScalar.Create(context.Runtime.Contains(Arg(0), input)); break;
                case "has": yield return JqScalar.Create(context.Runtime.Has(input, Arg(0))); break;
                case "indices":
                    if (UsesSubstringSearch(input, Arg(0)))
                        yield return context.Runtime.Indices(input, Arg(0));
                    else
                    {
                        _ = JqPathReads.TryIndex(input, Arg(0), false, context, out JqValue? got);
                        yield return got;
                    }
                    break;
                case "index":
                case "rindex":
                    if (UsesSubstringSearch(input, Arg(0)))
                        yield return context.Runtime.Index(input, Arg(0), name == "rindex");
                    else
                    {
                        _ = JqPathReads.TryIndex(input, Arg(0), false, context, out JqValue? values);
                        if (name == "rindex")
                            _ = JqPathReads.TrySlice(values, JqScalar.Create(-1), null, false, context, out values);
                        _ = JqPathReads.TryIndex(values, JqScalar.Create(0), false, context, out JqValue? got);
                        yield return got;
                    }
                    break;
                case "startswith": yield return JqScalar.Create(context.Runtime.StartsEndsWith(input, Arg(0), true)); break;
                case "endswith": yield return JqScalar.Create(context.Runtime.StartsEndsWith(input, Arg(0), false)); break;
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
                        List<JqValue> values = JqMatch.Split(context, input, Arg(0), Arg(1));
                        context.Budget.ReserveMetadata(64 + 24L * values.Count, 1);
                        var pieces = new JqArray();
                        foreach (JqValue piece in values)
                            pieces.Add(piece);
                        yield return pieces;
                        break;
                    }
                case "join": yield return context.Runtime.Join(input, Arg(0)); break;
                case "ascii_downcase": yield return context.Runtime.AsciiCase(input, true); break;
                case "ascii_upcase": yield return context.Runtime.AsciiCase(input, false); break;

                case "fromdate":
                case "fromdateiso8601": yield return JqTime.FromDateIso(context, input); break;
                case "todate":
                case "todateiso8601": yield return JqTime.ToDateIso(context, input); break;
                case "strptime": yield return JqTime.Strptime(context, input, Arg(0)); break;
                case "strftime": yield return JqTime.Strftime(context, input, Arg(0), false); break;
                case "strflocaltime": yield return JqTime.Strftime(context, input, Arg(0), true); break;
                case "gmtime": yield return JqTime.Gmtime(context, input); break;
                case "localtime": yield return JqTime.Localtime(context, input); break;
                case "mktime": yield return JqTime.MktimeUtc(context, input); break;
                case "builtins":
                {
                    var names = JqBuiltinRegistry.ListAll();
                    context.Budget.ReserveBytes(64 + 88L * names.Count);
                    var array = new JqArray();
                    foreach (var entry in names)
                    {
                        context.Budget.CheckCancellation();
                        array.Add(JqScalar.Create(entry, context.Budget));
                    }
                    yield return array;
                    break;
                }
                case "modulemeta":
                {
                    if (input is not JqString moduleName)
                        throw new JqException("modulemeta input module name must be a string");
                    if (context.ModuleLoader is null)
                        throw new JqException("modulemeta not available");
                    yield return await context.ModuleLoader.GetModuleMetadataAsync(moduleName.ToHost(context.Budget), context.Budget.CancellationToken).ConfigureAwait(false);
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
                case "input_filename": yield return context.InputFilename is string filename ? JqScalar.Create(filename, context.Budget) : null; break;
                case "input_line_number": yield return JqScalar.Create(context.InputLineNumber); break;
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
            await foreach (JqValue? probe in args[0].Evaluate(pair.Value, context, environment).ConfigureAwait(false))
                if (Truthy(probe))
                    yield return pair;
            yield break;
        }
        if (name is "ltrimstr" or "rtrimstr" or "trimstr")
        {
            await foreach (JqValue? affix in args[0].Evaluate(pair.Value, context, environment).ConfigureAwait(false))
            {
                bool left = name != "rtrimstr", right = name != "ltrimstr";
                if (pair.Value is not JqString text || affix is not JqString fix)
                    throw new JqException((left ? "startswith" : "endswith") + "() requires string inputs");
                JqValuePath current = pair;
                if (left && text.Utf8.Span.StartsWith(fix.Utf8.Span))
                {
                    int width = fix.ScalarLength;
                    if (!JqPathReads.TrySlicePath(current, JqScalar.Create(width), null, false, context, out JqValuePath? sliced))
                        yield break;
                    current = sliced;
                    text = current.Value as JqString ?? throw new InvalidOperationException("String slicing returned another value kind.");
                }
                if (right && text.Utf8.Span.EndsWith(fix.Utf8.Span))
                {
                    int end = text.ScalarLength - fix.ScalarLength;
                    if (!JqPathReads.TrySlicePath(current, null, JqScalar.Create(end), false, context, out JqValuePath? sliced))
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

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        yield return context.Runtime.FormatValue(format, input);
    }

}

internal sealed class InterpolatedStringFilter : JqFilter
{

    private abstract record Segment;

    private sealed record Literal(JqString Text) : Segment;

    private sealed record Interpolation(JqFilter Filter) : Segment;

    private readonly IReadOnlyList<Segment> _segments;
    private readonly string? _format;

    // The lexer has already split literal data from borrowed program fragments;
    // each interpolation is parsed once with the definition-site scopes, so unknown
    // names fail at compile time (stage 3) instead of per evaluation as stage 5.
    internal InterpolatedStringFilter(JqString literal, IReadOnlyList<JqStringInterpolation> interpolations,
        string? format, Func<string, JqFilter> parsePiece, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(parsePiece);
        _format = format;
        _segments = SplitTemplate();

        IReadOnlyList<Segment> SplitTemplate()
        {
            budget.ReserveMetadata(64, 1);
            var segments = new List<Segment>();
            int previous = 0;
            foreach (JqStringInterpolation interpolation in interpolations)
            {
                budget.CheckCancellation();
                AddLiteral(previous, interpolation.ByteOffset);
                budget.ReserveString(interpolation.Source.Length);
                budget.ReserveMetadata(64, 1);
                segments.Add(new Interpolation(parsePiece(interpolation.Source.ToString())));
                previous = interpolation.ByteOffset;
            }
            AddLiteral(previous, literal.ByteLength);
            return segments;

            void AddLiteral(int start, int end)
            {
                if (start == end) return;
                budget.ReserveMetadata(64, 1);
                segments.Add(new Literal(literal.SliceBytes(start, end, budget)));
            }
        }
    }

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (_segments.Count == 0) { yield return JqScalar.Create(string.Empty, context.Budget); yield break; }
        context.Budget.ReserveBytes(64L * _segments.Count);
        var iterators = new IAsyncEnumerator<JqString>?[_segments.Count];
        var pieces = new JqString?[_segments.Count];
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
                int length = 0;
                foreach (var piece in pieces)
                    length = JqBudget.BufferCapacity((long)length + (piece?.ByteLength ?? 0), 0);
                yield return JqString.Create(length, (pieces, context.Budget), static (target, state) =>
                {
                    int offset = 0;
                    foreach (JqString? piece in state.pieces)
                    {
                        state.Budget.CheckCancellation();
                        if (piece is null) continue;
                        piece.Utf8.Span.CopyTo(target.Slice(offset));
                        offset += piece.ByteLength;
                    }
                }, context.Budget);
            }
        }
        finally
        {
            // Earlier occurrences are inner (fast); unwind in that order.
            foreach (var iterator in iterators)
                if (iterator is not null) await iterator.DisposeAsync().ConfigureAwait(false);
        }

        async IAsyncEnumerable<JqString> Pieces(Segment segment)
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

        JqString Render(JqValue? value) => _format is null
            ? context.Runtime.ToNativeString(value)
            : context.Runtime.FormatValue(_format, value);
    }
}
