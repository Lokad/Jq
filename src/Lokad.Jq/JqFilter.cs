using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

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

    private static HashSet<string> CollectAll(IReadOnlyList<BindingPattern> alternatives)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (BindingPattern alternative in alternatives)
            alternative.CollectBoundNames(names);
        return names;
    }

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Every alternative name starts null so unmatched bindings read null.
        JqEnvironment prebound = environment;
        foreach (string name in _allNames)
            prebound = prebound.Extend(name, null);

        foreach (JsonNode? bound in source.Evaluate(input, context, environment))
        {
            bool completed = false;
            for (int index = 0; index < alternatives.Count && !completed; index++)
            {
                bool isLast = index == alternatives.Count - 1;
                using IEnumerator<JsonNode?> results = RunAlternative(alternatives[index], bound, prebound).GetEnumerator();
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

        IEnumerable<JsonNode?> RunAlternative(BindingPattern alternative, JsonNode? bound, JqEnvironment prebound)
        {
            foreach (JqEnvironment scope in Match(alternative, bound, prebound))
                foreach (JsonNode? output in body.Evaluate(input, context, scope))
                    yield return output;
        }

        IEnumerable<JqEnvironment> Match(BindingPattern pattern, JsonNode? value, JqEnvironment scope)
        {
            switch (pattern)
            {
                case VariablePattern variable:
                    yield return scope.Extend(variable.Name, context.Runtime.Clone(value));
                    break;
                case AliasPattern alias:
                    JqEnvironment aliased = scope.Extend(alias.Name, context.Runtime.Clone(value));
                    foreach (JqEnvironment inner in Match(alias.Inner, value, aliased))
                        yield return inner;
                    break;
                case ArrayPattern array:
                    foreach (JqEnvironment bound in MatchItems(array.Items, 0, value, scope))
                        yield return bound;
                    break;
                case ObjectPattern obj:
                    foreach (JqEnvironment bound in MatchProperties(obj.Properties, 0, value, scope))
                        yield return bound;
                    break;
                default:
                    throw new InvalidOperationException("Unknown binding pattern.");
            }
        }

        IEnumerable<JqEnvironment> MatchItems(IReadOnlyList<BindingPattern> items, int index, JsonNode? value, JqEnvironment scope)
        {
            if (index == items.Count)
            {
                yield return scope;
                yield break;
            }
            JsonNode? element = ElementAt(value, index);
            foreach (JqEnvironment bound in Match(items[index], element, scope))
                foreach (JqEnvironment rest in MatchItems(items, index + 1, value, bound))
                    yield return rest;
        }

        IEnumerable<JqEnvironment> MatchProperties(IReadOnlyList<ObjectPatternProperty> properties, int index, JsonNode? value, JqEnvironment scope)
        {
            if (index == properties.Count)
            {
                yield return scope;
                yield break;
            }
            ObjectPatternProperty property = properties[index];
            foreach (JqEnvironment keyed in MatchKeys(property, value, scope))
                foreach (JqEnvironment rest in MatchProperties(properties, index + 1, value, keyed))
                    yield return rest;
        }

        IEnumerable<JqEnvironment> MatchKeys(ObjectPatternProperty property, JsonNode? value, JqEnvironment scope)
        {
            foreach (JsonNode? keyValue in property.Key.Evaluate(value, context, scope))
            {
                if (!TryGetString(keyValue, out string key))
                    throw new JqRuntimeException($"Cannot use {TypeName(keyValue)} ({context.Runtime.ToJqString(keyValue)}) as object key");
                JsonNode? field = ExtractField(value, key);
                foreach (JqEnvironment bound in Match(property.Value, field, scope))
                    yield return bound;
            }
        }

        static JsonNode? ElementAt(JsonNode? value, int index) => value switch
        {
            JsonArray array => index < array.Count ? array[index] : null,
            null => null,
            _ => throw new JqRuntimeException($"cannot index {TypeName(value)} with number {index}"),
        };

        static JsonNode? ExtractField(JsonNode? value, string key) => value switch
        {
            JsonObject obj => obj.TryGetPropertyValue(key, out JsonNode? child) ? child : null,
            null => null,
            _ => throw new JqRuntimeException($"cannot index {TypeName(value)} with string \"{key}\""),
        };
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

    internal JqFunctionDefinition FunctionDefinition => Definition;

    internal JqFilter ContinuationBody => Continuation;

    internal DefFilter WithContinuation(JqFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new DefFilter(Definition, next);
    }
}

// A call to a user-defined function. Value arguments stream at the call
// site (cartesian combinations drive one body run each, in the engine's
// existing last-argument-outer order); filter arguments are captured with
// the caller environment and re-evaluated afresh at every use. The body
// runs against the call input in the closure environment, never the
// caller's later bindings.
internal sealed class UserCallFilter(string Name, int Arity, IReadOnlyList<JqFilter> Args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetFunction(Name, Arity, out JqUserClosure? closure) || closure is null)
            throw new JqException($"undefined function {Name}/{Arity}");
        if (Args.Count != closure.Definition.Arity)
            throw new JqException($"undefined function {Name}/{Arity}");
        foreach (JsonNode? value in EvaluateCallLoop(closure, input, Args, environment, context))
            yield return value;
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

    private static IEnumerable<JsonNode?[]> EvaluateValueCombos(IReadOnlyList<JqFilter> values, JsonNode? input, JqContext context, JqEnvironment environment)
    {
        var current = new JsonNode?[values.Count];
        return Combine(values.Count - 1);

        IEnumerable<JsonNode?[]> Combine(int index)
        {
            if (index < 0)
            {
                yield return (JsonNode?[])current.Clone();
                yield break;
            }
            foreach (JsonNode? value in values[index].Evaluate(input, context, environment))
            {
                current[index] = value;
                foreach (JsonNode?[] combo in Combine(index - 1))
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (var value in left.Evaluate(input, context, environment))
            yield return value;
        foreach (var value in right.Evaluate(input, context, environment))
            yield return value;
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
                if (value is JsonArray arr && TryGetInt(key, out var ix))
                {
                    if (ix < 0) ix = arr.Count + ix;
                    yield return ix >= 0 && ix < arr.Count ? context.Runtime.Clone(arr[ix]) : null;
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
}

internal sealed class SliceFilter(JqFilter source, JqFilter? start, JqFilter? end, bool optional) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Bound-major order: each start combines with every end, then every
        // source value, matching key-object construction with backtracking.
        foreach (var startIndex in StartIndexes())
            foreach (var endBound in EndBounds())
                foreach (var value in source.Evaluate(input, context, environment))
                {
                    int fromStart = startIndex;
                    int endIndex;
                    if (value is JsonArray arr)
                    {
                        endIndex = endBound ?? arr.Count;
                        NormalizeRange(arr.Count, ref fromStart, ref endIndex);
                        var result = new JsonArray();
                        for (var i = fromStart; i < endIndex; i++)
                            result.Add(context.Runtime.Clone(arr[i]));
                        yield return result;
                    }
                    else if (TryGetString(value, out var text))
                    {
                        var runeCount = text.EnumerateRunes().Count();
                        endIndex = endBound ?? runeCount;
                        NormalizeRange(runeCount, ref fromStart, ref endIndex);
                        var offset = 0;
                        var first = 0;
                        var index = 0;
                        foreach (var rune in text.EnumerateRunes())
                        {
                            if (index == fromStart) first = offset;
                            if (index++ == endIndex) break;
                            offset += rune.Utf16SequenceLength;
                        }
                        if (fromStart == runeCount) first = offset;
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

        IEnumerable<int> StartIndexes()
        {
            if (start == null)
            {
                yield return 0;
                yield break;
            }
            foreach (var bound in start.Evaluate(input, context, environment))
                yield return bound == null ? 0 : (int)Number(bound);
        }

        // A null item means the container length; null values still map to zero.
        IEnumerable<int?> EndBounds()
        {
            if (end == null)
            {
                yield return null;
                yield break;
            }
            foreach (var bound in end.Evaluate(input, context, environment))
                yield return bound == null ? 0 : (int)Number(bound);
        }
    }

    private static void NormalizeRange(int count, ref int start, ref int end)
    {
        if (start < 0) start = count + start;
        if (end < 0) end = count + end;
        start = Math.Clamp(start, 0, count);
        end = Math.Clamp(end, 0, count);
        if (end < start) end = start;
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
}

// Pre-order depth-first traversal: the value itself, then each child in
// order, matching recursive descent through iteration.
internal sealed class RecursiveDescentFilter : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        yield return context.Runtime.Clone(input);
        if (input is JsonArray array)
        {
            foreach (JsonNode? child in array)
                foreach (JsonNode? descendant in new RecursiveDescentFilter().Evaluate(child, context, environment))
                    yield return descendant;
        }
        else if (input is JsonObject obj)
        {
            foreach (var property in obj)
                foreach (JsonNode? descendant in new RecursiveDescentFilter().Evaluate(property.Value, context, environment))
                    yield return descendant;
        }
    }
}

internal sealed class ArrayFilter(JqFilter item) : JqFilter
{
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Value operators distribute with the left operand inner (fast),
        // matching reversed call prelude order with backtracking. Boolean
        // and/or keep their pairwise shape pending a later increment.
        if (op is "and" or "or")
        {
            foreach (var l in left.Evaluate(input, context, environment))
                foreach (var r in right.Evaluate(input, context, environment))
                    yield return Eval(l, r);
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
                "and" => JsonValue.Create(Truthy(l) && Truthy(r)),
                "or" => JsonValue.Create(Truthy(l) || Truthy(r)),
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
            yield return op switch
            {
                "-" => JsonValue.Create(-Number(value)),
                "not" => JsonValue.Create(!Truthy(value)),
                _ => throw new JqException($"unsupported unary operator {op}")
            };
        }
    }
}

internal sealed class IfFilter(
    IReadOnlyList<(JqFilter Condition, JqFilter Then)> branches,
    JqFilter otherwise) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (var (condition, then) in branches)
        {
            if (condition.Evaluate(input, context, environment).Any(Truthy))
            {
                foreach (var value in then.Evaluate(input, context, environment))
                    yield return value;
                yield break;
            }
        }

        foreach (var value in otherwise.Evaluate(input, context, environment))
            yield return value;
    }

    // Rewrites tail positions (branch bodies and the final else) while
    // leaving conditions untouched.
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

        IEnumerable<JsonNode?> EvaluateWith(JsonNode?[] combo)
        {
            JsonNode? Arg(int i) => combo[i];

            switch (name)
            {
                case "length": yield return JsonValue.Create(Length(input)); break;
                case "type": yield return JsonValue.Create(TypeName(input)); break;
                case "not": yield return JsonValue.Create(!Truthy(input)); break;
                case "tonumber": yield return JsonValue.Create(ToNumber(input)); break;
                case "toboolean": yield return JsonValue.Create(ToBoolean(input)); break;
                case "tostring": yield return JsonValue.Create(context.Runtime.ToJqString(input)); break;
                case "tojson": yield return JsonValue.Create(context.Runtime.Serialize(input, false, null, false)); break;
                case "fromjson": yield return context.Runtime.ParseJson(String(input)); break;
                case "abs": yield return JsonValue.Create(Math.Abs(Number(input))); break;
                case "floor": yield return JsonValue.Create(Math.Floor(Number(input))); break;
                case "sqrt": yield return JsonValue.Create(Math.Sqrt(Number(input))); break;
                case "add": yield return context.Runtime.AddAll(input); break;
                case "flatten": yield return context.Runtime.Flatten(input); break;
                case "min": yield return context.Runtime.MinMax(input, false); break;
                case "max": yield return context.Runtime.MinMax(input, true); break;
                case "reverse": yield return context.Runtime.Reverse(input); break;
                case "contains": yield return JsonValue.Create(context.Runtime.Contains(input, Arg(0))); break;
                case "inside": yield return JsonValue.Create(context.Runtime.Contains(Arg(0), input)); break;
                case "indices": yield return context.Runtime.Indices(input, Arg(0)); break;
                case "index": yield return context.Runtime.Index(input, Arg(0)); break;
                case "startswith": yield return JsonValue.Create(String(input).StartsWith(String(Arg(0)), StringComparison.Ordinal)); break;
                case "endswith": yield return JsonValue.Create(String(input).EndsWith(String(Arg(0)), StringComparison.Ordinal)); break;
                case "ltrimstr": yield return JsonValue.Create(TrimString(input, Arg(0), true, false)); break;
                case "rtrimstr": yield return JsonValue.Create(TrimString(input, Arg(0), false, true)); break;
                case "trimstr": yield return JsonValue.Create(TrimString(input, Arg(0), true, true)); break;
                case "trim": yield return JsonValue.Create(String(input).Trim()); break;
                case "ltrim": yield return JsonValue.Create(String(input).TrimStart()); break;
                case "rtrim": yield return JsonValue.Create(String(input).TrimEnd()); break;
                case "explode": yield return context.Runtime.Explode(input); break;
                case "implode": yield return context.Runtime.Implode(input); break;
                case "split": yield return context.Runtime.Split(input, Arg(0)); break;
                case "join": yield return context.Runtime.Join(input, Arg(0)); break;
                case "ascii_downcase": yield return JsonValue.Create(String(input).ToLowerInvariant()); break;
                case "ascii_upcase": yield return JsonValue.Create(String(input).ToUpperInvariant()); break;
                case "range":
                    foreach (var value in context.Runtime.Range(combo.Select(item => new List<JsonNode?> { item }).ToList()))
                        yield return value;
                    break;
                case "any": yield return JsonValue.Create(AnyAll(input, true)); break;
                case "all": yield return JsonValue.Create(AnyAll(input, false)); break;
                case "fromdate":
                case "fromdateiso8601": yield return JsonValue.Create(DateTimeOffset.Parse(String(input), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds()); break;
                case "todate":
                case "todateiso8601": yield return JsonValue.Create(DateTimeOffset.FromUnixTimeSeconds((long)Number(input)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)); break;
                case "strptime": yield return context.Runtime.Strptime(input, Arg(0)); break;
                case "strftime": yield return JsonValue.Create(UnixDate(input).ToString(context.Runtime.ConvertDateFormat(String(Arg(0))), CultureInfo.InvariantCulture)); break;
                default: throw new JqException($"unsupported function {name}");
            }
        }
    }
}

internal sealed class FormatFilter(string format) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        yield return JsonValue.Create(context.Runtime.Format(format, input));
    }

}

internal sealed class InterpolatedStringFilter(string template, string? format) : JqFilter
{
    private abstract record Segment;

    private sealed record Literal(string Text) : Segment;

    private sealed record Interpolation(JqFilter Filter) : Segment;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        // Split once per evaluation; each interpolation parses a single filter.
        var segments = new List<Segment>();
        var literal = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '\\' && i + 1 < template.Length && template[i + 1] == '(')
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
                    throw new JqException("unterminated string interpolation");
                if (literal.Length > 0)
                {
                    segments.Add(new Literal(literal.ToString()));
                    literal.Clear();
                }
                var parsed = new JqParser(template[start..i], context.ProgramSource, environment, context.Budget).Parse();
                segments.Add(new Interpolation(parsed));
            }
            else
            {
                literal.Append(template[i]);
            }
        }
        if (literal.Length > 0)
            segments.Add(new Literal(literal.ToString()));

        foreach (var text in Combine(segments.Count - 1))
            yield return JsonValue.Create(text);

        string Render(JsonNode? value) => format is null
            ? context.Runtime.ToJqString(value)
            : context.Runtime.Format(format, value);

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
            if (segments[index] is Literal run)
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
            if (segments[index] is Interpolation interpolation)
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
