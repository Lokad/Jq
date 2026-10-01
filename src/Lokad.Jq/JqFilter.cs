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
    public IEnumerable<JsonNode?> Evaluate(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        context.Budget.EnterEvaluation();
        try
        {
            foreach (var value in EvaluateCore(input, context, environment))
            {
                context.Budget.ChargeNode();
                if (TryGetString(value, out var text)) context.Budget.ChargeString(text.Length);
                yield return value;
            }
        }
        finally
        {
            context.Budget.LeaveEvaluation();
        }
    }

    /// <summary>Evaluates within the caller's shared budget; charge before growing intermediate values.</summary>
    protected abstract IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment);

    internal IEnumerable<JqValuePath> EvaluatePaths(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        context.Budget.EnterEvaluation();
        try
        {
            foreach (JqValuePath path in EvaluatePathsCore(pair, context, environment))
            {
                context.Budget.ChargeNode();
                yield return path;
            }
        }
        finally
        {
            context.Budget.LeaveEvaluation();
        }
    }

    // Default path transparency: ordinary evaluation, keeping tracking
    // only for outputs identical to the incoming value. Filters that build
    // fresh containers override PreservesPathIdentity so their results travel
    // untracked and fail at the next path boundary, like the reference,
    // even when a rebuilt container coincides with the input value.
    internal virtual bool PreservesPathIdentity => true;

    protected virtual IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        foreach (JsonNode? value in Evaluate(pair.Value, context, environment))
            yield return new JqValuePath(pair.Segments, value, pair.Tracked && PreservesPathIdentity && PathIntact(context.Runtime, pair.Value, value));
    }

    // Path intactness mirrors upstream bitwise identity for doubles (so
    // signed zeros stay distinct) while keeping value equality elsewhere.
    internal static bool PathIntact(JqRuntime runtime, JsonNode? expected, JsonNode? actual)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (expected is JsonValue a && actual is JsonValue b
            && a.TryGetValue<double>(out double x) && b.TryGetValue<double>(out double y))
            return BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y);
        return runtime.JsonEquals(actual, expected);
    }
}

internal sealed class IdentityFilter : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        yield return context.Runtime.Clone(input);
    }
}

internal sealed class LiteralFilter(JsonNode? value) : JqFilter
{
    internal JsonNode? Value => value;

    // Upstream identity keeps immediates (numbers, booleans, null) intact
    // by value while heap values (strings, arrays, objects) break on fresh
    // copies, so literals follow the same split.
    internal override bool PreservesPathIdentity =>
        value is null || (value is JsonValue scalar && !TryGetString(scalar, out _));

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        yield return context.Runtime.Clone(value);
    }
}

internal sealed class VariableFilter(string name) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (!environment.TryGetValue(name, out JsonNode? value))
            throw new JqException($"undefined variable ${name}");
        yield return context.Runtime.Clone(value);
    }
}

// Source location: a parse-time constant capturing the file and line where
// `$__loc__` occurs. The file is the module display path or `<top-level>`
// for inline filters; the line is 1-based.
internal sealed class LocFilter(string file, int line) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        var location = new JsonObject
        {
            ["file"] = JsonValue.Create(file),
            ["line"] = JsonValue.Create(line),
        };
        context.Budget.ChargeNode();
        context.Budget.ChargeString(file.Length + 8);
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

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (JsonNode? bound in source.Evaluate(input, context, environment))
            foreach (var (_, output) in DriveAlternatives(alternatives, _allNames, bound, environment, context, scope => body.Evaluate(input, context, scope)))
                yield return output;
    }

    // Shared alternative driver for bindings and reductions: every name
    // starts null, the first non-erroring alternative wins (even when it
    // yields nothing), and catchable match or body errors retry later
    // alternatives. Reduction drivers reuse it with collecting bodies.
    internal static IEnumerable<(JqEnvironment Scope, JsonNode? Output)> DriveAlternatives(IReadOnlyList<BindingPattern> alternatives, HashSet<string> allNames, JsonNode? bound, JqEnvironment environment, JqContext context, Func<JqEnvironment, IEnumerable<JsonNode?>> body)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        ArgumentNullException.ThrowIfNull(allNames);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        JqEnvironment prebound = environment;
        foreach (string name in allNames)
            prebound = prebound.Extend(name, null);
        bool completed = false;
        for (int index = 0; index < alternatives.Count && !completed; index++)
        {
            bool isLast = index == alternatives.Count - 1;
            using IEnumerator<JqEnvironment> scopes = Match(alternatives[index], bound, prebound, context).GetEnumerator();
            while (true)
            {
                bool moved;
                try
                {
                    moved = scopes.MoveNext();
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
                using IEnumerator<JsonNode?> results = body(scopes.Current).GetEnumerator();
                while (true)
                {
                    bool produced;
                    try
                    {
                        produced = results.MoveNext();
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

    private static IEnumerable<JqEnvironment> Match(BindingPattern pattern, JsonNode? value, JqEnvironment scope, JqContext context)
    {
        switch (pattern)
        {
            case VariablePattern variable:
                yield return scope.Extend(variable.Name, context.Runtime.Clone(value));
                break;
            case AliasPattern alias:
                JqEnvironment aliased = scope.Extend(alias.Name, context.Runtime.Clone(value));
                foreach (JqEnvironment inner in Match(alias.Inner, value, aliased, context))
                    yield return inner;
                break;
            case ArrayPattern array:
                foreach (JqEnvironment bound in MatchItems(array.Items, 0, value, scope, context))
                    yield return bound;
                break;
            case ObjectPattern obj:
                foreach (JqEnvironment bound in MatchProperties(obj.Properties, 0, value, scope, context))
                    yield return bound;
                break;
            default:
                throw new InvalidOperationException("Unknown binding pattern.");
        }
    }

    private static IEnumerable<JqEnvironment> MatchItems(IReadOnlyList<BindingPattern> items, int index, JsonNode? value, JqEnvironment scope, JqContext context)
    {
        if (index == items.Count)
        {
            yield return scope;
            yield break;
        }
        JsonNode? element = ElementAt(value, index);
        foreach (JqEnvironment bound in Match(items[index], element, scope, context))
            foreach (JqEnvironment rest in MatchItems(items, index + 1, value, bound, context))
                yield return rest;
    }

    private static IEnumerable<JqEnvironment> MatchProperties(IReadOnlyList<ObjectPatternProperty> properties, int index, JsonNode? value, JqEnvironment scope, JqContext context)
    {
        if (index == properties.Count)
        {
            yield return scope;
            yield break;
        }
        ObjectPatternProperty property = properties[index];
        foreach (JqEnvironment keyed in MatchKeys(property, value, scope, context))
            foreach (JqEnvironment rest in MatchProperties(properties, index + 1, value, keyed, context))
                yield return rest;
    }

    private static IEnumerable<JqEnvironment> MatchKeys(ObjectPatternProperty property, JsonNode? value, JqEnvironment scope, JqContext context)
    {
        foreach (JsonNode? keyValue in property.Key.Evaluate(value, context, scope))
        {
            if (!TryGetString(keyValue, out string key))
                throw new JqRuntimeException($"Cannot use {TypeName(keyValue)} ({context.Runtime.ToJqString(keyValue)}) as object key");
            JsonNode? field = ExtractField(value, key);
            foreach (JqEnvironment bound in Match(property.Value, field, scope, context))
                yield return bound;
        }
    }

    private static JsonNode? ElementAt(JsonNode? value, int index) => value switch
    {
        JsonArray array => index < array.Count ? array[index] : null,
        null => null,
        _ => throw new JqRuntimeException($"cannot index {TypeName(value)} with number {index}"),
    };

    private static JsonNode? ExtractField(JsonNode? value, string key) => value switch
    {
        JsonObject obj => obj.TryGetPropertyValue(key, out JsonNode? child) ? child : null,
        null => null,
        _ => throw new JqRuntimeException($"cannot index {TypeName(value)} with string \"{key}\""),
    };

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        // The source binds in value mode; the body extends the incoming path
        // against the outer input, mirroring upstream binding behavior.
        JqEnvironment prebound = environment;
        foreach (string name in _allNames)
            prebound = prebound.Extend(name, null);
        foreach (JsonNode? bound in source.Evaluate(pair.Value, context, environment))
        {
            bool completed = false;
            for (int index = 0; index < alternatives.Count && !completed; index++)
            {
                bool isLast = index == alternatives.Count - 1;
                using IEnumerator<JqValuePath> results = RunPathAlternative(alternatives[index], bound, prebound, pair, context).GetEnumerator();
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = results.MoveNext();
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

    private IEnumerable<JqValuePath> RunPathAlternative(BindingPattern alternative, JsonNode? bound, JqEnvironment prebound, JqValuePath pair, JqContext context)
    {
        foreach (JqEnvironment scope in Match(alternative, bound, prebound, context))
            foreach (JqValuePath next in body.EvaluatePaths(new JqValuePath(pair.Segments, pair.Value, pair.Tracked), context, scope))
                yield return next;
    }
}

// A `def name[(params)]: body; rest` definition: forges no values itself but
// extends the environment with the definition before running the rest.
// Closures forged later capture this extended environment, so each `def`
// observes exactly the bindings visible at its own site.
internal sealed class DefFilter(JqFunctionDefinition Definition, JqFilter Continuation) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JqEnvironment extended = environment.ExtendFunction(Definition.Name, Definition.Arity, Definition);
        foreach (JsonNode? value in Continuation.Evaluate(input, context, extended))
            yield return value;
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        JqEnvironment extended = environment.ExtendFunction(Definition.Name, Definition.Arity, Definition);
        foreach (JqValuePath next in Continuation.EvaluatePaths(pair, context, extended))
            yield return next;
    }

    internal JqFunctionDefinition FunctionDefinition => Definition;

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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!TryResolve(environment, Name, Arity, out JqUserClosure? closure) || closure is null)
            throw new JqException($"undefined function {Name}/{Arity}");
        if (Args.Count != closure.Definition.Arity)
            throw new JqException($"undefined function {Name}/{Arity}");
        foreach (JsonNode? value in EvaluateCallLoop(closure, input, Args, environment, context))
            yield return value;
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!TryResolve(environment, Name, Arity, out JqUserClosure? closure) || closure is null)
            throw new JqException($"undefined function {Name}/{Arity}");
        if (Args.Count != closure.Definition.Arity)
            throw new JqException($"undefined function {Name}/{Arity}");
        foreach (JqValuePath next in EvaluatePathsCallLoop(closure, pair, Args, environment, context))
            yield return next;
    }

    private static bool TryResolve(JqEnvironment environment, string name, int arity, out JqUserClosure? closure)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains("::", System.StringComparison.Ordinal))
            return environment.TryGetFunctionQualified(name, arity, out closure);

        return environment.TryGetFunction(name, arity, out closure);
    }

    // Shared call driver: streams one body run per value combination and
    // chains tail-position self-calls on an explicit heap stack instead of
    // nesting evaluator frames. Pipe right-hand sides reuse this per left
    // value so abandoned sources can never lose values.
    internal static IEnumerable<JsonNode?> EvaluateCallLoop(JqUserClosure closure, JsonNode? callInput, IReadOnlyList<JqFilter> args, JqEnvironment callerEnvironment, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(callerEnvironment);
        ArgumentNullException.ThrowIfNull(context);
        JqFunctionDefinition definition = closure.Definition;
        PartitionArgs(definition, args, callerEnvironment, out List<JqFilter> values, out List<int> valuePositions, out JqFilterClosure?[] filters);

        // The initial value combinations stream lazily; only tail-call
        // argument lists materialize (bounded by per-frame budget charges).
        // A pending stack preserves argument order across tail chains.
        var pending = new Stack<CallFrame>();
        foreach (JsonNode?[] combo in EvaluateValueCombos(values, callInput, context, callerEnvironment))
        {
            CallFrame? current = new(callInput, combo, valuePositions, filters);
            while (true)
            {
                if (current is null)
                {
                    if (!pending.TryPop(out current))
                        break;
                }
                using IEnumerator<JsonNode?> outputs = EvaluateFrame(definition, closure, current, context).GetEnumerator();
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = outputs.MoveNext();
                    }
                    catch (TailCallSignal signal) when (signal.Definition.Name == definition.Name && signal.Definition.Arity == definition.Arity)
                    {
                        current = PushSignalFrames(signal, definition, context, pending);
                        break;
                    }
                    if (!moved)
                    {
                        current = null;
                        break;
                    }
                    yield return outputs.Current;
                }
            }
        }
    }

    internal string FunctionName => Name;

    internal int FunctionArity => Arity;

    internal IReadOnlyList<JqFilter> CallArgs => Args;

    private sealed record CallFrame(JsonNode? Input, JsonNode?[] Values, IReadOnlyList<int> ValuePositions, JqFilterClosure?[] Filters);

    // Path-mode call driver: mirrors the value loop, threading segment
    // lists through body runs and tail chains instead of bare values.
    internal static IEnumerable<JqValuePath> EvaluatePathsCallLoop(JqUserClosure closure, JqValuePath pair, IReadOnlyList<JqFilter> args, JqEnvironment callerEnvironment, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(callerEnvironment);
        ArgumentNullException.ThrowIfNull(context);
        JqFunctionDefinition definition = closure.Definition;
        PartitionArgs(definition, args, callerEnvironment, out List<JqFilter> values, out List<int> valuePositions, out JqFilterClosure?[] filters);
        var pending = new Stack<PathCallFrame>();
        foreach (JsonNode?[] combo in EvaluateValueCombos(values, pair.Value, context, callerEnvironment))
        {
            PathCallFrame? current = new(pair, combo, valuePositions, filters);
            while (true)
            {
                if (current is null)
                {
                    if (!pending.TryPop(out current))
                        break;
                }
                using IEnumerator<JqValuePath> outputs = EvaluatePathsFrame(definition, closure, current, context).GetEnumerator();
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = outputs.MoveNext();
                    }
                    catch (TailCallSignal signal) when (signal.Definition.Name == definition.Name && signal.Definition.Arity == definition.Arity)
                    {
                        current = PushSignalPathFrames(signal, pair, context, pending);
                        break;
                    }
                    if (!moved)
                    {
                        current = null;
                        break;
                    }
                    yield return outputs.Current;
                }
            }
        }
    }

    private sealed record PathCallFrame(JqValuePath Pair, JsonNode?[] Values, IReadOnlyList<int> ValuePositions, JqFilterClosure?[] Filters);

    private static IEnumerable<JqValuePath> EvaluatePathsFrame(JqFunctionDefinition definition, JqUserClosure closure, PathCallFrame frame, JqContext context)
    {
        JqEnvironment scope = closure.Environment;
        for (int position = 0; position < frame.ValuePositions.Count; position++)
            scope = scope.Extend(definition.Parameters[frame.ValuePositions[position]].Name, context.Runtime.Clone(frame.Values[position]));
        for (int index = 0; index < frame.Filters.Length; index++)
            if (frame.Filters[index] is not null)
                scope = scope.ExtendFilter(definition.Parameters[index].Name, frame.Filters[index]!);
        foreach (JqValuePath next in definition.Body.EvaluatePaths(new JqValuePath(frame.Pair.Segments, frame.Pair.Value, frame.Pair.Tracked), context, scope))
            yield return next;
    }

    private static PathCallFrame? PushSignalPathFrames(TailCallSignal signal, JqValuePath pair, JqContext context, Stack<PathCallFrame> pending)
    {
        var combos = new List<JsonNode?[]>();
        foreach (JsonNode?[] combo in EvaluateValueCombos(signal.ValueArgs, signal.Input, context, signal.Environment))
        {
            context.Budget.ChargeNode();
            combos.Add(combo);
        }
        // Tail frames restart the body against the signal input but keep
        // accumulating onto the call-site segments, exactly like the
        // initial frames do.
        for (int index = combos.Count - 1; index >= 1; index--)
        {
            context.Budget.ChargeNode();
            pending.Push(new PathCallFrame(new JqValuePath(pair.Segments, signal.Input, pair.Tracked), combos[index], signal.ValuePositions, signal.FilterArgs));
        }
        if (combos.Count == 0)
            return null;
        context.Budget.ChargeNode();
        return new PathCallFrame(new JqValuePath(pair.Segments, signal.Input, pair.Tracked), combos[0], signal.ValuePositions, signal.FilterArgs);
    }

    // Peeks up to two value combinations (budget-charged) to decide whether
    // a final pipe value may unwind flat. Returns false unless exactly one
    // combination exists; multi-combination tails stay nested and ordered.
    internal static bool TryPeekSingleCall(JqFunctionDefinition definition, IReadOnlyList<JqFilter> args, JsonNode? input, JqEnvironment environment, JqContext context, out List<JqFilter> values, out List<int> valuePositions, out JqFilterClosure?[] filters)
    {
        PartitionArgs(definition, args, environment, out values, out valuePositions, out filters);
        using IEnumerator<JsonNode?[]> combos = EvaluateValueCombos(values, input, context, environment).GetEnumerator();
        if (!combos.MoveNext())
            return false;
        context.Budget.ChargeNode();
        return !combos.MoveNext();
    }

    internal static void PartitionArgs(JqFunctionDefinition definition, IReadOnlyList<JqFilter> args, JqEnvironment environment, out List<JqFilter> values, out List<int> valuePositions, out JqFilterClosure?[] filters)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        if (args.Count != definition.Arity)
            throw new JqException($"undefined function {definition.Name}/{definition.Arity}");
        filters = new JqFilterClosure?[definition.Arity];
        values = new List<JqFilter>();
        valuePositions = new List<int>();
        for (int index = 0; index < definition.Arity; index++)
        {
            if (definition.Parameters[index].IsValue)
            {
                values.Add(args[index]);
                valuePositions.Add(index);
            }
            else
            {
                filters[index] = new JqFilterClosure(args[index], environment);
            }
        }
    }

    private static IEnumerable<JsonNode?> EvaluateFrame(JqFunctionDefinition definition, JqUserClosure closure, CallFrame frame, JqContext context)
    {
        JqEnvironment scope = closure.Environment;
        for (int position = 0; position < frame.ValuePositions.Count; position++)
            scope = scope.Extend(definition.Parameters[frame.ValuePositions[position]].Name, context.Runtime.Clone(frame.Values[position]));
        for (int index = 0; index < frame.Filters.Length; index++)
            if (frame.Filters[index] is not null)
                scope = scope.ExtendFilter(definition.Parameters[index].Name, frame.Filters[index]!);
        foreach (JsonNode? value in definition.Body.Evaluate(frame.Input, context, scope))
            yield return value;
    }

    private static CallFrame? PushSignalFrames(TailCallSignal signal, JqFunctionDefinition definition, JqContext context, Stack<CallFrame> pending)
    {
        var combos = new List<JsonNode?[]>();
        foreach (JsonNode?[] combo in EvaluateValueCombos(signal.ValueArgs, signal.Input, context, signal.Environment))
        {
            context.Budget.ChargeNode();
            combos.Add(combo);
        }
        for (int index = combos.Count - 1; index >= 1; index--)
        {
            context.Budget.ChargeNode();
            pending.Push(new CallFrame(signal.Input, combos[index], signal.ValuePositions, signal.FilterArgs));
        }
        if (combos.Count == 0)
            return null;
        context.Budget.ChargeNode();
        return new CallFrame(signal.Input, combos[0], signal.ValuePositions, signal.FilterArgs);
    }

    // User-call value arguments combine first-argument-outer like the
    // reference range/3 vector, unlike the last-argument-outer generic
    // builtin prelude that the remaining builtins share with the reference.
    private static IEnumerable<JsonNode?[]> EvaluateValueCombos(IReadOnlyList<JqFilter> values, JsonNode? input, JqContext context, JqEnvironment environment)
    {
        var current = new JsonNode?[values.Count];
        return Combine(0);

        IEnumerable<JsonNode?[]> Combine(int index)
        {
            if (index >= values.Count)
            {
                yield return (JsonNode?[])current.Clone();
                yield break;
            }
            foreach (JsonNode? value in values[index].Evaluate(input, context, environment))
            {
                current[index] = value;
                foreach (JsonNode?[] combo in Combine(index + 1))
                    yield return combo;
            }
        }
    }
}

// A use of a filter parameter: re-evaluates the captured argument against
// the current input in the caller environment from the call site.
internal sealed class FilterParamCallFilter(string Name) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetFilter(Name, out JqFilterClosure? closure) || closure is null)
            throw new JqException($"undefined filter {Name}");
        foreach (JsonNode? value in closure.Filter.Evaluate(input, context, closure.Environment))
            yield return value;
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetFilter(Name, out JqFilterClosure? closure) || closure is null)
            throw new JqException($"undefined filter {Name}");
        foreach (JqValuePath next in closure.Filter.EvaluatePaths(pair, context, closure.Environment))
            yield return next;
    }
}

// A tail-position self-call unwinds to the owning call loop instead of
// nesting another evaluator frame, so tail-recursive runs take heap and
// budget rather than C# stack. Deliberately not a JqException: quota,
// cancellation, and language-error boundaries must never observe it.
internal sealed class TailCallSignal(
    JqFunctionDefinition Definition,
    JsonNode? Input,
    JqEnvironment Environment,
    IReadOnlyList<JqFilter> ValueArgs,
    IReadOnlyList<int> ValuePositions,
    JqFilterClosure?[] FilterArgs) : Exception
{
    internal JqFunctionDefinition Definition { get; } = Definition ?? throw new ArgumentNullException(nameof(Definition));
    internal JsonNode? Input { get; } = Input;
    internal JqEnvironment Environment { get; } = Environment ?? throw new ArgumentNullException(nameof(Environment));
    internal IReadOnlyList<JqFilter> ValueArgs { get; } = ValueArgs ?? throw new ArgumentNullException(nameof(ValueArgs));
    internal IReadOnlyList<int> ValuePositions { get; } = ValuePositions ?? throw new ArgumentNullException(nameof(ValuePositions));
    internal JqFilterClosure?[] FilterArgs { get; } = FilterArgs ?? throw new ArgumentNullException(nameof(FilterArgs));
}

// A syntactically marked tail call: partitions arguments exactly like a
// regular call, then signals the owning loop with unevaluated value
// arguments and use-site input so the next frame restarts flat.
internal sealed class TailSelfCallFilter(JqFunctionDefinition Definition, IReadOnlyList<JqFilter> Args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        UserCallFilter.PartitionArgs(Definition, Args, environment, out List<JqFilter> values, out List<int> valuePositions, out JqFilterClosure?[] filters);
        throw new TailCallSignal(Definition, input, environment, values, valuePositions, filters);
    }

    internal JqFunctionDefinition TailDefinition => Definition;

    internal IReadOnlyList<JqFilter> TailArgs => Args;
}

internal sealed class PipeFilter(JqFilter left, JqFilter right) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (var value in left.Evaluate(input, context, environment))
            foreach (var output in right.Evaluate(value, context, environment))
                yield return output;
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath left in left.EvaluatePaths(pair, context, environment))
            foreach (JqValuePath right in right.EvaluatePaths(left, context, environment))
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

// A pipe whose right-hand side is a tail-position self-call on the pure tail
// spine (the rewrite only descends through positions that hold no pending
// output work, so every ancestor up to the owning call loop is abandonment-
// free by construction). A lookahead separates the final source value from
// earlier ones: earlier values run nested per-value loops, while a final
// value with a single argument combination unwinds to the owning loop and
// runs flat. Anything else stays a correct depth-bounded call.
internal sealed class PipeTailLoop(JqFilter Source, JqFunctionDefinition Definition, IReadOnlyList<JqFilter> Args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetFunction(Definition.Name, Definition.Arity, out JqUserClosure? closure) || closure is null)
            throw new JqException($"undefined function {Definition.Name}/{Definition.Arity}");
        if (Args.Count != closure.Definition.Arity)
            throw new JqException($"undefined function {Definition.Name}/{Definition.Arity}");
        using IEnumerator<JsonNode?> sources = Source.Evaluate(input, context, environment).GetEnumerator();
        if (!sources.MoveNext())
            yield break;
        while (true)
        {
            JsonNode? current = sources.Current;
            if (!sources.MoveNext())
            {
                if (UserCallFilter.TryPeekSingleCall(closure.Definition, Args, current, environment, context, out List<JqFilter> values, out List<int> valuePositions, out JqFilterClosure?[] filters))
                    throw new TailCallSignal(closure.Definition, current, environment, values, valuePositions, filters);
                foreach (JsonNode? value in UserCallFilter.EvaluateCallLoop(closure, current, Args, environment, context))
                    yield return value;
                yield break;
            }
            foreach (JsonNode? value in UserCallFilter.EvaluateCallLoop(closure, current, Args, environment, context))
                yield return value;
        }
    }
}

internal sealed class CommaFilter(JqFilter left, JqFilter right) : JqFilter
{
    internal JqFilter Left => left;
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (var value in left.Evaluate(input, context, environment))
            yield return value;
        foreach (var value in right.Evaluate(input, context, environment))
            yield return value;
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath left in left.EvaluatePaths(pair, context, environment))
            yield return left;
        foreach (JqValuePath right in right.EvaluatePaths(pair, context, environment))
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (var value in source.Evaluate(input, context, environment))
        {
            if (value is JsonObject obj)
                yield return context.Runtime.Clone(obj.TryGetPropertyValue(name, out var child) ? child : null);
            else if (value == null)
                yield return null;
            else if (!optional)
                throw new JqRuntimeException($"cannot index {TypeName(value)} with string \"{name}\"");
        }
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath source in source.EvaluatePaths(pair, context, environment))
        {
            RequireTracked(source, new KeySegment(name), context);
            if (source.Value is JsonObject obj)
                yield return new JqValuePath(Extend(source.Segments, new KeySegment(name)), obj.TryGetPropertyValue(name, out JsonNode? child) ? child : null, true);
            else if (source.Value == null)
                yield return new JqValuePath(Extend(source.Segments, new KeySegment(name)), null, true);
            else
                throw new JqRuntimeException($"cannot index {TypeName(source.Value)} with string \"{name}\"");
        }
    }
}

internal sealed class IndexFilter(JqFilter source, JqFilter index, bool optional) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Key-major order: each key combines with every source value,
        // matching index-then-source evaluation with backtracking.
        foreach (var key in index.Evaluate(input, context, environment))
            foreach (var value in source.Evaluate(input, context, environment))
            {
                if (value is JsonArray arr && TryGetArrayIndex(key, out long ix))
                {
                    long resolved = ix < 0 ? arr.Count + ix : ix;
                    yield return resolved >= 0 && resolved < arr.Count ? context.Runtime.Clone(arr[(int)resolved]) : null;
                }
                else if (value is JsonArray && key is JsonValue nan && nan.TryGetValue<double>(out double missing) && double.IsNaN(missing))
                {
                    // A NaN index reads null instead of failing.
                    yield return null;
                }
                else if (value is JsonObject obj && TryGetString(key, out var name))
                {
                    yield return context.Runtime.Clone(obj.TryGetPropertyValue(name, out var child) ? child : null);
                }
                else if (value == null)
                {
                    yield return null;
                }
                else if (!optional)
                {
                    throw new JqRuntimeException($"cannot index {TypeName(value)}");
                }
            }
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment))
            foreach (JsonNode? key in index.Evaluate(pair.Value, context, environment))
            {
                JqValueSegment segment = PathSegmentFor(key);
                RequireTracked(pair, segment, context);
                if (segment is KeySegment name && pair.Value is JsonObject obj)
                    yield return new JqValuePath(Extend(pair.Segments, segment), obj.TryGetPropertyValue(name.Key, out JsonNode? child) ? child : null, true);
                else if (segment is IndexSegment number && pair.Value is JsonArray arr && !number.IsNaN)
                {
                    long resolved = number.Index < 0 ? arr.Count + number.Index : number.Index;
                    yield return new JqValuePath(Extend(pair.Segments, segment), resolved >= 0 && resolved < arr.Count ? arr[(int)resolved] : null, true);
                }
                else if (segment is IndexSegment nan && nan.IsNaN && pair.Value is JsonArray)
                    yield return new JqValuePath(Extend(pair.Segments, segment), null, true);
                else if (pair.Value == null)
                    yield return new JqValuePath(Extend(pair.Segments, segment), null, true);
                else if (segment is KeySegment field)
                    throw new JqRuntimeException($"cannot index {TypeName(pair.Value)} with string \"{field.Key}\"");
                else
                    throw new JqRuntimeException($"cannot index {TypeName(pair.Value)}");
            }
    }

    private static JqValueSegment PathSegmentFor(JsonNode? key)
    {
        if (TryGetString(key, out string? name))
            return new KeySegment(name);
        if (TryGetIndex(key, out long index, out bool isNaN))
            return new IndexSegment(index, isNaN);
        return new InvalidSegment(key?.DeepClone());
    }
}

internal sealed class SliceFilter(JqFilter source, JqFilter? start, JqFilter? end, bool optional) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Bound-major order: each start combines with every end, then every
        // source value, matching key-object construction with backtracking.
        foreach (double startBound in StartBounds())
            foreach (double? endBound in EndBounds())
                foreach (var value in source.Evaluate(input, context, environment))
                {
                    if (value is JsonArray arr)
                    {
                        JqPaths.ResolveSlice(arr.Count, startBound, endBound, out int from, out int to);
                        var result = new JsonArray();
                        for (var i = from; i < to; i++)
                            result.Add(context.Runtime.Clone(arr[i]));
                        yield return result;
                    }
                    else if (TryGetString(value, out var text))
                    {
                        var runeCount = text.EnumerateRunes().Count();
                        JqPaths.ResolveSlice(runeCount, startBound, endBound, out int from, out int to);
                        var offset = 0;
                        var first = 0;
                        var index = 0;
                        foreach (var rune in text.EnumerateRunes())
                        {
                            if (index == from) first = offset;
                            if (index++ == to) break;
                            offset += rune.Utf16SequenceLength;
                        }
                        if (from == runeCount) first = offset;
                        context.Budget.ChargeString(offset - first);
                        yield return JsonValue.Create(text[first..offset]);
                    }
                    else if (value == null)
                    {
                        yield return null;
                    }
                    else if (!optional)
                    {
                        throw new JqRuntimeException($"cannot slice {TypeName(value)}");
                    }
                }

        IEnumerable<double> StartBounds()
        {
            if (start == null)
            {
                yield return 0;
                yield break;
            }
            foreach (var bound in start.Evaluate(input, context, environment))
            {
                if (bound == null)
                {
                    yield return 0;
                    continue;
                }
                if (bound is JsonValue edge && edge.TryGetValue<double>(out double nan) && double.IsNaN(nan))
                {
                    yield return 0;
                    continue;
                }
                yield return Number(bound);
            }
        }

        // A missing, null, or NaN end means the container length, matching
        // slice defaults; other bounds keep their fractional values for the
        // shared start-down/end-up resolution.
        IEnumerable<double?> EndBounds()
        {
            if (end == null)
            {
                yield return null;
                yield break;
            }
            foreach (var bound in end.Evaluate(input, context, environment))
            {
                if (bound == null || (bound is JsonValue edge && edge.TryGetValue<double>(out double nan) && double.IsNaN(nan)))
                    yield return null;
                else
                    yield return Number(bound);
            }
        }
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment))
            foreach (double? lower in SliceBoundValues(start, pair.Value, context, environment))
                foreach (double? upper in SliceBoundValues(end, pair.Value, context, environment))
                {
                    var segment = new SliceSegment(lower, upper);
                    RequireTracked(pair, segment, context);
                    if (pair.Value is JsonArray arr)
                    {
                        JqPaths.ResolveSlice(arr.Count, lower, upper, out int from, out int to);
                        var part = new JsonArray();
                        for (int position = from; position < to; position++)
                            part.Add(arr[position]?.DeepClone());
                        yield return new JqValuePath(Extend(pair.Segments, segment), part, true);
                    }
                    else if (pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out string? text) && text is not null)
                    {
                        var runes = new List<System.Text.Rune>();
                        foreach (var rune in text.EnumerateRunes())
                            runes.Add(rune);
                        JqPaths.ResolveSlice(runes.Count, lower, upper, out int from, out int to);
                        var builder = new System.Text.StringBuilder();
                        for (int position = from; position < to; position++)
                            builder.Append(runes[position].ToString());
                        yield return new JqValuePath(Extend(pair.Segments, segment), JsonValue.Create(builder.ToString()), true);
                    }
                    else if (pair.Value == null)
                        yield return new JqValuePath(Extend(pair.Segments, segment), null, true);
                    else
                        throw new JqRuntimeException($"cannot slice {TypeName(pair.Value)}");
                }
    }

    private static IEnumerable<double?> SliceBoundValues(JqFilter? bound, JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (bound == null)
        {
            yield return null;
            yield break;
        }
        foreach (JsonNode? edge in bound.Evaluate(input, context, environment))
        {
            if (edge is null)
            {
                yield return null;
                continue;
            }
            if (edge is JsonValue number && number.TryGetValue<double>(out double index) && double.IsNaN(index))
            {
                yield return null;
                continue;
            }
            yield return Number(edge);
        }
    }
}

internal sealed class IteratorFilter(JqFilter source, bool optional) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (var value in source.Evaluate(input, context, environment))
        {
            if (value is JsonArray arr)
            {
                foreach (var child in arr)
                    yield return context.Runtime.Clone(child);
            }
            else if (value is JsonObject obj)
            {
                foreach (var child in obj)
                    yield return context.Runtime.Clone(child.Value);
            }
            else if (!optional && value != null)
                throw new JqRuntimeException($"cannot iterate over {TypeName(value)}");
        }
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath pair in source.EvaluatePaths(outer, context, environment))
        {
            if ((pair.Value is JsonArray || pair.Value is JsonObject) && !pair.Tracked)
                throw new JqException(InvalidIterate(pair.Value, context));
            if (pair.Value is JsonArray arr)
            {
                for (int index = 0; index < arr.Count; index++)
                    yield return new JqValuePath(Extend(pair.Segments, new IndexSegment(index, false)), arr[index], true);
            }
            else if (pair.Value is JsonObject obj)
            {
                foreach (var property in obj)
                    yield return new JqValuePath(Extend(pair.Segments, new KeySegment(property.Key)), property.Value, true);
            }
            else if (pair.Value is not null)
            {
                if (!pair.Tracked)
                    throw new JqException(InvalidIterate(pair.Value, context));
                throw new JqRuntimeException($"cannot iterate over {TypeName(pair.Value)}");
            }
        }
    }
}

// Pre-order depth-first traversal: the value itself, then each child in
// order, matching recursive descent through iteration.
internal sealed class RecursiveDescentFilter : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Iterative pre-order: depth follows value nesting, which setpath-built
        // trees push far past the ingress depth cap, so nested enumerator frames
        // are not an option.
        var stack = new Stack<JsonNode?>();
        stack.Push(input);
        while (stack.Count > 0)
        {
            context.Budget.CheckCancellation();
            JsonNode? current = stack.Pop();
            yield return context.Runtime.Clone(current);
            if (current is JsonArray array)
            {
                for (int index = array.Count - 1; index >= 0; index--)
                    stack.Push(array[index]);
            }
            else if (current is JsonObject obj)
            {
                var values = new List<JsonNode?>();
                foreach (var property in obj)
                    values.Add(property.Value);
                for (int index = values.Count - 1; index >= 0; index--)
                    stack.Push(values[index]);
            }
        }
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath descendant in Expand(pair, context))
            yield return descendant;

    }
    // Pre-order structural threading: the pair itself, then each child with
    // an extended path. Trackedness flows through untouched; scalars and
    // nulls simply have no children. Children ride a heap stack so deep value
    // trees never cost CLR frames.
    private static IEnumerable<JqValuePath> Expand(JqValuePath pair, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(context);
        var stack = new Stack<JqValuePath>();
        stack.Push(pair);
        while (stack.Count > 0)
        {
            context.Budget.CheckCancellation();
            JqValuePath current = stack.Pop();
            yield return current;
            if (current.Value is null)
                continue;
            if (!current.Tracked)
                throw new JqException(InvalidIterate(current.Value, context));
            if (current.Value is JsonArray arr)
            {
                for (int index = arr.Count - 1; index >= 0; index--)
                    stack.Push(new JqValuePath(Extend(current.Segments, new IndexSegment(index, false)), arr[index], true));
            }
            else if (current.Value is JsonObject obj)
            {
                var entries = new List<(string Key, JsonNode? Value)>();
                foreach (var property in obj)
                    entries.Add((property.Key, property.Value));
                for (int index = entries.Count - 1; index >= 0; index--)
                    stack.Push(new JqValuePath(Extend(current.Segments, new KeySegment(entries[index].Key)), entries[index].Value, true));
            }
        }
    }
}

internal sealed class ArrayFilter(JqFilter item) : JqFilter
{
    internal JqFilter Item => item;

    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        var array = new JsonArray();
        foreach (var value in item.Evaluate(input, context, environment))
            array.Add(context.Runtime.Clone(value));
        yield return array;
    }
}

internal sealed record ObjectProperty(string? StaticKey, JqFilter? KeyFilter, JqFilter Value);

internal sealed class ObjectFilter(IReadOnlyList<ObjectProperty> properties) : JqFilter
{
    internal IReadOnlyList<ObjectProperty> Properties => properties;

    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (var obj in Build(0, new JsonObject()))
            yield return obj;

        IEnumerable<JsonObject> Build(int index, JsonObject current)
        {
            if (index == properties.Count)
            {
                yield return current;
                yield break;
            }

            ObjectProperty property = properties[index];
            if (property is { StaticKey: string key, KeyFilter: null })
            {
                // Lazy cartesian: an empty value stream yields no objects.
                foreach (var value in property.Value.Evaluate(input, context, environment))
                {
                    if (context.Runtime.Clone(current) is not JsonObject next)
                        throw new InvalidOperationException("Expected object clone.");
                    next[key] = context.Runtime.Clone(value);
                    foreach (var obj in Build(index + 1, next))
                        yield return obj;
                }
            }
            else if (property.KeyFilter is JqFilter keyFilter)
            {
                foreach (var keyValue in keyFilter.Evaluate(input, context, environment))
                {
                    if (!TryGetString(keyValue, out string keyName))
                        throw new JqRuntimeException($"Cannot use {TypeName(keyValue)} ({context.Runtime.ToJqString(keyValue)}) as object key");
                    foreach (var value in property.Value.Evaluate(input, context, environment))
                    {
                        if (context.Runtime.Clone(current) is not JsonObject next)
                            throw new InvalidOperationException("Expected object clone.");
                        next[keyName] = context.Runtime.Clone(value);
                        foreach (var obj in Build(index + 1, next))
                            yield return obj;
                    }
                }
            }
        }
    }
}

internal sealed class BinaryFilter(JqFilter left, string op, JqFilter right) : JqFilter
{
    // Arithmetic builds fresh values and breaks path identity; boolean
    // connectives and comparisons agree with the reference through boolean
    // singletons on both sides, so they keep the default rule.
    internal override bool PreservesPathIdentity => op is "and" or "or" or "==" or "!=" or "<" or "<=" or ">" or ">=";

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Value operators distribute with the left operand inner (fast),
        // matching reversed call prelude order with backtracking. Boolean
        // and/or short-circuit per left value: a decided left never touches
        // the right side, while an undecided left streams it as booleans.
        if (op is "and")
        {
            foreach (JsonNode? l in left.Evaluate(input, context, environment))
            {
                if (!Truthy(l))
                {
                    yield return JsonValue.Create(false);
                    continue;
                }
                foreach (JsonNode? r in right.Evaluate(input, context, environment))
                    yield return JsonValue.Create(Truthy(r));
            }
            yield break;
        }
        if (op is "or")
        {
            foreach (JsonNode? l in left.Evaluate(input, context, environment))
            {
                if (Truthy(l))
                {
                    yield return JsonValue.Create(true);
                    continue;
                }
                foreach (JsonNode? r in right.Evaluate(input, context, environment))
                    yield return JsonValue.Create(Truthy(r));
            }
            yield break;
        }
        else
        {
            foreach (var r in right.Evaluate(input, context, environment))
                foreach (var l in left.Evaluate(input, context, environment))
                    yield return Eval(l, r);
        }

        JsonNode? Eval(JsonNode? l, JsonNode? r)
        {
            return op switch
            {
                "+" => context.Runtime.Add(l, r),
                "-" => context.Runtime.Subtract(l, r),
                "*" => context.Runtime.Multiply(l, r),
                "/" => Divide(l, r),
                "%" => Modulo(l, r),
                "==" => JsonValue.Create(context.Runtime.JsonEquals(l, r)),
                "!=" => JsonValue.Create(!context.Runtime.JsonEquals(l, r)),
                "<" => JsonValue.Create(Compare(l, r) < 0),
                "<=" => JsonValue.Create(Compare(l, r) <= 0),
                ">" => JsonValue.Create(Compare(l, r) > 0),
                ">=" => JsonValue.Create(Compare(l, r) >= 0),
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
}

// Postfix `?` on any term: catchable evaluation failures yield nothing.
// Quota, compile, cancellation, and host failures still propagate.
internal sealed class OptionalFilter(JqFilter inner) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        using IEnumerator<JsonNode?> results = inner.Evaluate(input, context, environment).GetEnumerator();
        while (true)
        {
            bool moved;
            try
            {
                moved = results.MoveNext();
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

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath next in inner.EvaluatePaths(pair, context, environment))
            yield return next;
    }

    internal JqFilter Inner => inner;

    internal OptionalFilter WithInner(JqFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new OptionalFilter(next);
    }
}

// Defined-or: non-false, non-null left outputs pass through; the right side
// runs only when no such output exists.
internal sealed class AlternativeFilter(JqFilter left, JqFilter right) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        bool found = false;
        foreach (JsonNode? value in left.Evaluate(input, context, environment))
        {
            if (Truthy(value))
            {
                found = true;
                yield return value;
            }
        }
        if (!found)
            foreach (JsonNode? value in right.Evaluate(input, context, environment))
                yield return value;
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        bool found = false;
        foreach (JqValuePath left in left.EvaluatePaths(pair, context, environment))
        {
            if (Truthy(left.Value))
            {
                found = true;
                yield return left;
            }
        }
        if (!found)
            foreach (JqValuePath right in right.EvaluatePaths(pair, context, environment))
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (var value in inner.Evaluate(input, context, environment))
        {
            if (op == "-")
            {
                if (TypeName(value) != "number")
                    throw new JqRuntimeException(TypeName(value) + " (" + context.Runtime.Serialize(value, false, null, false) + ") cannot be negated");
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Reference distribution: every condition output routes independently,
        // truthy outputs run the branch body while falsy outputs fall through
        // to the rest of the chain; an empty condition yields nothing at all.
        foreach (var value in RunBranch(0))
            yield return value;

        IEnumerable<JsonNode?> RunBranch(int index)
        {
            if (index >= branches.Count)
            {
                foreach (var value in otherwise.Evaluate(input, context, environment))
                    yield return value;
                yield break;
            }
            var (condition, then) = branches[index];
            foreach (JsonNode? probe in condition.Evaluate(input, context, environment))
            {
                if (Truthy(probe))
                {
                    foreach (var value in then.Evaluate(input, context, environment))
                        yield return value;
                }
                else
                {
                    foreach (var value in RunBranch(index + 1))
                        yield return value;
                }
            }
        }
    }

    // Rewrites tail positions (branch bodies and the final else) while
    // leaving conditions untouched.
    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        foreach (JqValuePath next in RunBranchPaths(0))
            yield return next;

        IEnumerable<JqValuePath> RunBranchPaths(int index)
        {
            if (index >= branches.Count)
            {
                foreach (JqValuePath next in otherwise.EvaluatePaths(pair, context, environment))
                    yield return next;
                yield break;
            }
            var (condition, then) = branches[index];
            foreach (JsonNode? probe in condition.Evaluate(pair.Value, context, environment))
            {
                if (Truthy(probe))
                {
                    foreach (JqValuePath next in then.EvaluatePaths(pair, context, environment))
                        yield return next;
                }
                else
                {
                    foreach (JqValuePath next in RunBranchPaths(index + 1))
                        yield return next;
                }
            }
        }
    }

    internal IfFilter WithTails(Func<JqFilter, JqFilter> rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        var resolved = new List<(JqFilter Condition, JqFilter Then)>();
        foreach (var (condition, then) in branches)
            resolved.Add((condition, rewrite(then)));
        return new IfFilter(resolved, rewrite(otherwise));
    }
}

internal sealed class FunctionFilter(string name, IReadOnlyList<JqFilter> args) : JqFilter
{
    // Freshly computed values break path identity: only pass-through
    // selectors (upstream definitions over select), subvalue extrema, and
    // stderr keep the default value-based rule. Everything else built here
    // (conversions, string operations, math, search predicates, formats,
    // dates, and the remaining rebuilt containers) travels untracked.
    internal override bool PreservesPathIdentity => name is ("stderr" or "min" or "max" or "arrays" or "objects" or "iterables" or "booleans" or "numbers" or "strings" or "nulls" or "values" or "scalars" or "finites" or "normals");

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        if (name == "empty")
            yield break;

        if (name == "select")
        {
            if (args.Count != 1)
                throw new JqException("select expects one argument");
            // Predicates can yield multiple results; consume them without buffering.
            foreach (var value in args[0].Evaluate(input, context, environment))
                if (Truthy(value))
                    yield return context.Runtime.Clone(input);
            yield break;
        }

        if (name == "error")
        {
            if (args.Count > 1)
                throw new JqException("error expects at most one argument");
            // Bare `error` raises the input; otherwise the first argument
            // value wins and the rest never runs. An empty argument stream
            // yields nothing at all.
            if (args.Count == 0)
                throw NewError(context, input);
            foreach (var value in args[0].Evaluate(input, context, environment))
                throw NewError(context, value);
            yield break;
        }

        if (name == "halt")
            throw new JqHaltException(0, null);

        if (name == "halt_error")
        {
            if (args.Count > 1)
                throw new JqException("halt_error expects at most one argument");
            // Bare `halt_error` exits 5 with the input rendered; otherwise
            // the first code value wins and the rest never runs.
            if (args.Count == 0)
                throw NewHalt(context, input, 5);
            foreach (var value in args[0].Evaluate(input, context, environment))
                throw NewHalt(context, input, HaltCode(value));
            yield break;
        }


        // Cartesian argument streams: the last argument is outer (slow) and
        // the first argument inner (fast), matching reversed call prelude
        // order with backtracking. An empty argument yields no outputs.
        foreach (var combo in ArgumentCombos())
            foreach (var output in EvaluateWith(combo))
                yield return output;

        yield break;

        IEnumerable<JsonNode?[]> ArgumentCombos()
        {
            var current = new JsonNode?[args.Count];
            return Combine(args.Count - 1);

            IEnumerable<JsonNode?[]> Combine(int index)
            {
                if (index < 0)
                {
                    yield return (JsonNode?[])current.Clone();
                    yield break;
                }
                foreach (var value in args[index].Evaluate(input, context, environment))
                {
                    current[index] = value;
                    foreach (var combo in Combine(index - 1))
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

        static JqErrorException NewError(JqContext context, JsonNode? payload)
        {
            JsonNode? held = context.Runtime.Clone(payload);
            string rendered = held is null ? "null"
                : TryGetString(held, out string? text) ? text
                : context.Runtime.ToJqString(held);
            string message = "error: " + rendered;
            context.Budget.ChargeString(message.Length);
            return new JqErrorException(held, message);
        }

        IEnumerable<JsonNode?> EvaluateWith(JsonNode?[] combo)
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
                case "env": yield return context.Runtime.Clone(context.Variables.TryGetValue("ENV", out JsonNode? environment) ? environment : new JsonObject()); break;
                case "stderr":
                    if (TryGetString(input, out string? message) && message is not null)
                        context.EmitStderr(Utf8Text.Encode(message).ToArray());
                    else
                        context.EmitStderr(context.Runtime.SerializeUtf8(input, false, null, false).ToArray());
                    yield return context.Runtime.Clone(input);
                    break;
                case "tonumber": yield return context.Runtime.ToJsonNumber(input); break;
                case "toboolean": yield return JsonValue.Create(context.Runtime.ToJsonBoolean(input)); break;
                case "utf8bytelength": yield return JsonValue.Create(Utf8ByteLength(input, context)); break;
                case "tostring": yield return JsonValue.Create(context.Runtime.ToJqString(input)); break;
                case "tojson": yield return JsonValue.Create(context.Runtime.Serialize(input, false, null, false)); break;
                case "fromjson": yield return context.Runtime.ParseJson(String(input)); break;
                case "min": yield return context.Runtime.MinMax(input, false); break;
                case "max": yield return context.Runtime.MinMax(input, true); break;
                case "reverse": yield return context.Runtime.Reverse(input); break;
                case "sort": yield return context.Runtime.SortArray(input); break;
                case "unique": yield return context.Runtime.UniqueArray(input); break;
                case "keys": yield return context.Runtime.Keys(input, true); break;
                case "keys_unsorted": yield return context.Runtime.Keys(input, false); break;
                case "to_entries": yield return context.Runtime.ToEntries(input); break;
                case "from_entries": yield return context.Runtime.FromEntries(input); break;
                case "arrays": if (TypeName(input) == "array") yield return context.Runtime.Clone(input); break;
                case "objects": if (TypeName(input) == "object") yield return context.Runtime.Clone(input); break;
                case "iterables": if (TypeName(input) is "array" or "object") yield return context.Runtime.Clone(input); break;
                case "booleans": if (TypeName(input) == "boolean") yield return context.Runtime.Clone(input); break;
                case "numbers": if (TypeName(input) == "number") yield return context.Runtime.Clone(input); break;
                case "strings": if (TypeName(input) == "string") yield return context.Runtime.Clone(input); break;
                case "nulls": if (input is null) yield return null; break;
                case "values": if (input is not null) yield return context.Runtime.Clone(input); break;
                case "scalars": if (TypeName(input) is not ("array" or "object")) yield return context.Runtime.Clone(input); break;
                case "finites": if (JqMath.Classify("isfinite", input)) yield return context.Runtime.Clone(input); break;
                case "normals": if (JqMath.Classify("isnormal", input)) yield return context.Runtime.Clone(input); break;
                case "_negate": if (TypeName(input) != "number") throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") cannot be negated"); yield return JsonValue.Create(-Number(input)); break;
                case "_strindices":
                    if (!TryGetString(input, out string? strHaystack) || strHaystack is null)
                        throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") cannot be searched, as it is not a string");
                    if (!TryGetString(Arg(0), out string? strNeedle) || strNeedle is null)
                        throw new JqRuntimeException(TypeName(Arg(0)) + " (" + context.Runtime.Serialize(Arg(0), false, null, false) + ") is not a string");
                    yield return context.Runtime.Indices(input, Arg(0));
                    break;
                case "format":
                    if (!TryGetString(Arg(0), out string? formatName) || formatName is null)
                        throw new JqRuntimeException(TypeName(Arg(0)) + " (" + context.Runtime.Serialize(Arg(0), false, null, false) + ") is not a valid format");
                    if (formatName != "json" && formatName != "text" && formatName != "csv" && formatName != "tsv" && formatName != "html" && formatName != "uri" && formatName != "urid" && formatName != "sh" && formatName != "base64" && formatName != "base64d")
                        throw new JqRuntimeException(formatName + " is not a valid format");
                    yield return JsonValue.Create(context.Runtime.Format(formatName, input));
                    break;
                case "contains": yield return JsonValue.Create(context.Runtime.Contains(input, Arg(0))); break;
                case "inside": yield return JsonValue.Create(context.Runtime.Contains(Arg(0), input)); break;
                case "has": yield return JsonValue.Create(context.Runtime.Has(input, Arg(0))); break;
                case "indices": yield return context.Runtime.Indices(input, Arg(0)); break;
                case "index": yield return context.Runtime.Index(input, Arg(0)); break;
                case "startswith": yield return JsonValue.Create(StartsEndsWith(input, Arg(0), true)); break;
                case "endswith": yield return JsonValue.Create(StartsEndsWith(input, Arg(0), false)); break;
                case "ltrimstr": yield return JsonValue.Create(TrimAffix(input, Arg(0), true, false)); break;
                case "rtrimstr": yield return JsonValue.Create(TrimAffix(input, Arg(0), false, true)); break;
                case "trimstr": yield return JsonValue.Create(TrimAffix(input, Arg(0), true, true)); break;
                case "trim": yield return JsonValue.Create(TrimSides(input, true, true)); break;
                case "ltrim": yield return JsonValue.Create(TrimSides(input, true, false)); break;
                case "rtrim": yield return JsonValue.Create(TrimSides(input, false, true)); break;
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
                        var pieces = new JsonArray();
                        foreach (string piece in JqMatch.SplitPieces(context, input, Arg(0), Arg(1)))
                            pieces.Add(JsonValue.Create(piece));
                        yield return pieces;
                        break;
                    }
                case "join": yield return context.Runtime.Join(input, Arg(0)); break;
                case "ascii_downcase": yield return JsonValue.Create(context.Runtime.AsciiCase(String(input), true)); break;
                case "ascii_upcase": yield return JsonValue.Create(context.Runtime.AsciiCase(String(input), false)); break;

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
                    var array = new JsonArray();
                    foreach (var entry in names)
                    {
                        context.Budget.ChargeNode();
                        context.Budget.ChargeString(entry.Length);
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
                    yield return context.ModuleLoader.GetModuleMetadataSync(moduleName);
                    break;
                }
                case "input":
                {
                    var cursor = context.InputCursor ?? throw new JqException("input not available");
                    if (cursor.TryPullSync(out JsonNode? next))
                        yield return next;
                    else
                        throw new JqException("break");
                    break;
                }
                case "inputs":
                {
                    var cursor = context.InputCursor ?? throw new JqException("input not available");
                    JsonNode? next;
                    while (cursor.TryPullSync(out next))
                        yield return next;
                    break;
                }
                case "input_filename": yield return context.InputFilename is string filename ? JsonValue.Create(filename) : null; break;
                case "input_line_number": yield return JsonValue.Create(context.InputLineNumber); break;
                default: throw new JqException($"unsupported function {name}");
            }
        }
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (name == "select")
        {
            if (args.Count != 1)
                throw new JqException("select expects one argument");
            // Predicates run in value mode; surviving pairs keep paths.
            foreach (JsonNode? probe in args[0].Evaluate(pair.Value, context, environment))
                if (Truthy(probe))
                    yield return pair;
            yield break;
        }
        if (name == "getpath")
        {
            if (args.Count != 1)
                throw new JqException("getpath expects one argument");
            if (!pair.Tracked)
                throw new JqException(InvalidResult(pair.Value, context));
            // Path synthesis: the argument supplies segments directly.
            foreach (JsonNode? paths in args[0].Evaluate(pair.Value, context, environment))
            {
                List<JqValueSegment> extra = ParsePathValue(paths, context);
                var segments = new List<JqValueSegment>(pair.Segments.Count + extra.Count);
                foreach (JqValueSegment existing in pair.Segments)
                    segments.Add(existing);
                foreach (JqValueSegment added in extra)
                    segments.Add(added);
                yield return new JqValuePath(segments, JqPathReads.GetPath(pair.Value, extra), true);
            }
            yield break;
        }
        foreach (JqValuePath next in base.EvaluatePathsCore(pair, context, environment))
            yield return next;
    }
}
internal sealed class FormatFilter(string format) : JqFilter
{
    // Formatted encodings build fresh strings, so results travel untracked.
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        yield return JsonValue.Create(context.Runtime.Format(format, input));
    }

}

internal sealed class InterpolatedStringFilter : JqFilter
{
    // Templates always build fresh strings, so results travel untracked.
    internal override bool PreservesPathIdentity => false;

    private abstract record Segment;

    private sealed record Literal(string Text) : Segment;

    private sealed record Interpolation(JqFilter Filter) : Segment;

    private readonly IReadOnlyList<Segment> _segments;
    private readonly string? _format;

    // Splits once at construction so malformed templates fail before evaluation;
    // each interpolation is parsed once with the definition-site scopes, so unknown
    // names fail at compile time (stage 3) instead of per evaluation as stage 5.
    internal InterpolatedStringFilter(string template, string? format, JqSourceSpan span, JqProgramSource program, Func<string, JqSourceSpan, JqFilter> parsePiece)
    {
        ArgumentNullException.ThrowIfNull(parsePiece);
        _format = format;
        _segments = SplitTemplate(template, span, program, parsePiece);
    }

    private static IReadOnlyList<Segment> SplitTemplate(string template, JqSourceSpan span, JqProgramSource program, Func<string, JqSourceSpan, JqFilter> parsePiece)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(parsePiece);
        var segments = new List<Segment>();
        var literal = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == (char)92 && i + 1 < template.Length && template[i + 1] == (char)92)
            {
                literal.Append(template, i, 2);
                i++;
            }
            else if (template[i] == (char)92 && i + 1 < template.Length && template[i + 1] == (char)40)
            {
                var start = i + 2;
                var depth = 1;
                i = start;
for (; i < template.Length; i++)
                {
                    if (template[i] == '"')
                    {
                        i++;
                        while (i < template.Length && template[i] != '"')
                        {
                            if (template[i] == '\\' && i + 1 < template.Length) i++;
                            i++;
                        }
                    }
                    else if (template[i] == '(') depth++;
                    else if (template[i] == ')' && --depth == 0) break;
                }
                if (depth != 0)
                    throw new JqCompileException("unterminated string interpolation", span, program);
                if (literal.Length > 0)
                {
                    segments.Add(new Literal(Lexer.DecodeLiterals(literal.ToString())));
                    literal.Clear();
                }
                segments.Add(new Interpolation(parsePiece(template[start..i], span)));
            }
            else
            {
                literal.Append(template[i]);
            }
        }
        if (literal.Length > 0)
            segments.Add(new Literal(Lexer.DecodeLiterals(literal.ToString())));
        return segments;
    }

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var text in Combine(_segments.Count - 1))
            yield return JsonValue.Create(text);
        string Render(JsonNode? value) => _format is null
            ? context.Runtime.ToJqString(value)
            : context.Runtime.Format(_format, value);

        // Later occurrences are outer (slow); the first is inner (fast),
        // matching nested concatenation with backtracking. An empty
        // interpolation yields no strings.
        IEnumerable<string> Combine(int index)
        {
            if (index < 0)
            {
                yield return "";
                yield break;
            }
            if (_segments[index] is Literal run)
            {
                foreach (var prefix in Combine(index - 1))
                {
                    StringBuilder assembled = new StringBuilder(prefix.Length + run.Text.Length);
                    assembled.Append(prefix);
                    context.Budget.Append(assembled, run.Text.AsSpan());
                    yield return context.Budget.Finish(assembled);
                }
                yield break;
            }
            if (_segments[index] is Interpolation interpolation)
            {
                foreach (var value in interpolation.Filter.Evaluate(input, context, environment))
                {
                    string rendered = Render(value);
                    foreach (var prefix in Combine(index - 1))
                    {
                        StringBuilder assembled = new StringBuilder(prefix.Length + rendered.Length);
                        assembled.Append(prefix);
                        context.Budget.Append(assembled, rendered.AsSpan());
                        yield return context.Budget.Finish(assembled);
                    }
                }
            }
        }
    }
}
