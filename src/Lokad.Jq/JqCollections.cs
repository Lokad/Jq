using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// `map(f)`: collects every f-output per element into a new array.
internal sealed class MapFilter(JqFilter Body) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var result = new JsonArray();
        context.Budget.CheckCancellation();
        foreach (JsonNode? element in Iterate(input, context, environment))
            foreach (JsonNode? value in Body.Evaluate(element, context, environment))
            {
                context.Budget.ReserveArrayItem(value);
                result.Add(context.Runtime.Clone(value));
            }
        yield return result;
    }

    internal static IEnumerable<JsonNode?> Iterate(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        foreach (JsonNode? value in new IteratorFilter(new IdentityFilter(), false).Evaluate(input, context, environment))
            yield return value;
    }
}

// `map_values(f)`: first f-output per element, dropping empties. Objects
// keep their keys; anything else follows iteration errors.
internal sealed class MapValuesFilter(JqFilter Body) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (input is JsonArray array)
        {
            var result = new JsonArray();
            context.Budget.CheckCancellation();
            foreach (JsonNode? element in array)
            {
                using IEnumerator<JsonNode?> outputs = Body.Evaluate(element, context, environment).GetEnumerator();
                if (outputs.MoveNext())
                {
                    context.Budget.ReserveArrayItem(outputs.Current);
                    result.Add(context.Runtime.Clone(outputs.Current));
                }
            }
            yield return result;
            yield break;
        }
        if (input is JsonObject obj)
        {
            var result = new JsonObject();
            context.Budget.CheckCancellation();
            foreach (var property in obj)
            {
                using IEnumerator<JsonNode?> outputs = Body.Evaluate(property.Value, context, environment).GetEnumerator();
                if (outputs.MoveNext())
                {
                    context.Budget.ReserveObjectItem(outputs.Current);
                    result.Add(property.Key, context.Runtime.Clone(outputs.Current));
                }
            }
            yield return result;
            yield break;
        }
        throw new JqRuntimeException($"cannot iterate over {TypeName(input)}");
    }
}

// `with_entries(f)`: entries through f and back, multi-outputs included.
internal sealed class WithEntriesFilter(JqFilter Body) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        context.Budget.ReserveBytes(64);
        var mapped = new List<JsonNode?>();
        foreach (JsonNode? entry in context.Runtime.ToEntries(input))
            foreach (JsonNode? value in Body.Evaluate(entry, context, environment))
            {
                context.Budget.ReserveBytes(32);
                mapped.Add(value);
            }
        yield return context.Runtime.FromEntryValues(mapped);
    }
}

// `in(xs)`: one membership boolean per collection value.
internal sealed class InFilter(JqFilter Containers) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? container in Containers.Evaluate(input, context, environment))
            yield return JsonValue.Create(context.Runtime.Has(container, input));
    }
}

// `add` and `add(f)`: folds a value stream with `+` from null.
internal sealed class AddValuesFilter(JqFilter Values) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        yield return context.Runtime.AddValues(Values.Evaluate(input, context, environment));
    }
}

// `flatten` and `flatten(depth)`: depth-limited flattening over arrays.
// Like the reference desugar, the negative guard compares with jq ordering:
// null, booleans, and NaN (which sorts as null) fail it, while strings,
// arrays, and objects sort above zero and recurse through depth - 1.
internal sealed class FlattenFilter(JqFilter? Depth) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    internal static bool TryDepthLevel(JsonNode? depth, out double level)
    {
        if (depth is JsonValue value)
        {
            if (value.TryGetValue<double>(out level))
                return true;
            if (value.TryGetValue<long>(out long whole))
            {
                level = whole;
                return true;
            }
            if (value.TryGetValue<int>(out int integer))
            {
                level = integer;
                return true;
            }
            if (value.TryGetValue<decimal>(out decimal dec))
            {
                level = (double)dec;
                return true;
            }
        }
        level = double.NaN;
        return false;
    }

    internal static bool IsBooleanValue(JsonNode? depth) =>
        depth is JsonValue value && value.TryGetValue<bool>(out _);



    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (Depth is null)
        {
            yield return context.Runtime.Flatten(input, JsonValue.Create(-1));
            yield break;
        }
        foreach (JsonNode? depth in Depth.Evaluate(input, context, environment))
        {
            if (TryDepthLevel(depth, out double level))
            {
                if (level < 0 || double.IsNaN(level))
                    throw new JqException("flatten depth must not be negative");
            }
            else if (depth is null || IsBooleanValue(depth))
            {
                throw new JqException("flatten depth must not be negative");
            }
            yield return context.Runtime.Flatten(input, depth);
        }
    }
}

// `transpose`: rows become columns, jagged rows padded with null.
internal sealed class TransposeFilter : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        int height = input is JsonArray array ? array.Count : input is JsonObject fields ? fields.Count
            : throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(input)}");
        int width = 0;
        foreach (JsonNode? row in Rows())
        {
            context.Budget.CheckCancellation();
            if (row is not JsonArray && row is not null)
                throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(row)}");
            int length = row is JsonArray present ? present.Count : 0;
            if (length > width)
                width = length;
            context.Budget.CheckCancellation();
        }
        context.Budget.ReserveBytes(64 + 24L * width);
        var result = new JsonArray();
        for (int column = 0; column < width; column++)
        {
            context.Budget.ReserveBytes(64 + 24L * height);
            var line = new JsonArray();
            foreach (JsonNode? row in Rows())
            {
                context.Budget.CheckCancellation();
                JsonNode? cell = row is JsonArray cells && column < cells.Count ? cells[column] : null;
                if (cell is JsonValue) context.Budget.ReserveBytes(64);
                line.Add(context.Budget.CopyValue(cell, 2));
            }
            result.Add(line);
        }
        yield return result;

        IEnumerable<JsonNode?> Rows()
        {
            if (input is JsonArray rows)
                foreach (JsonNode? row in rows) yield return row;
            else if (input is JsonObject properties)
                foreach (var property in properties) yield return property.Value;
        }
    }
}

// `range/1..3`: argument combinations stream first-argument-outer like the
// reference range vectors, unlike the last-argument-outer generic builtin
// prelude that the remaining builtins share with the reference.
internal sealed class RangeFilter(IReadOnlyList<JqFilter> args) : JqFilter
{
    // Generated scalars are always fresh, so results travel untracked.
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var current = new JsonNode?[args.Count];
        foreach (JsonNode? value in Generate(0)) yield return value;

        IEnumerable<JsonNode?> Generate(int index)
        {
            if (index >= args.Count)
            {
                double start = args.Count == 1 ? 0 : Bound(current[0]);
                double end = Bound(current[args.Count == 1 ? 0 : 1]);
                double step = args.Count > 2 ? Bound(current[2]) : 1;
                if (!(step > 0) && !(step < 0)) yield break;
                for (double value = start; step > 0 ? value < end : value > end; value += step)
                {
                    context.Budget.CheckCancellation();
                    yield return CreateNumber(value);
                }
                yield break;
            }
            foreach (JsonNode? value in args[index].Evaluate(input, context, environment))
            {
                current[index] = value;
                foreach (JsonNode? result in Generate(index + 1)) yield return result;
            }
        }

        static double Bound(JsonNode? value)
        {
            if (!TryCountLevel(value, out double bound)) throw new JqException("Range bounds must be numeric");
            return bound;
        }
    }
}

// `combinations` and `combinations(n)`: cartesian products of array rows.
internal sealed class CombinationsFilter(JqFilter? Count) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (Count is null)
        {
            // Like the reference length check, length-zero inputs yield
            // one empty combination instead of iterating.
            if (JqRuntime.Number(context.Runtime.Length(input)) == 0)
            {
                context.Budget.CheckCancellation();
                yield return new JsonArray();
                yield break;
            }
            if (input is not JsonArray matrix)
                throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(input)}");
            foreach (JsonNode? combo in Combos(matrix, context))
                yield return combo;
            yield break;
        }
        foreach (JsonNode? count in Count.Evaluate(input, context, environment))
        {
            // Like range bounds upstream, non-numbers fail with the range
            // diagnostic while numeric strings still coerce.
            if (!JqRuntime.TryCountLevel(count, out double level))
                throw new JqException("Range bounds must be numeric");
            // Like the reference [range(n)|$dot] prelude, the repetition count
            // is the number of range outputs: ceiling for positive fractionals.
            int times = level <= 0 || double.IsNaN(level) ? 0 : Math.Max(0, (int)Math.Ceiling(level));
            context.Budget.ReserveBytes(24L * times);
            var matrix = new List<JsonNode?>(times);
            for (int index = 0; index < times; index++)
            {
                context.Budget.CheckCancellation();
                matrix.Add(input);
            }
            foreach (JsonNode? combo in Combos(matrix, context))
                yield return combo;
        }
    }

    // Iterative odometer over row positions. Recursion depth here would be input
    // length, which must not translate into unbounded CLR stack depth, so
    // nested enumerator frames are not an option. Row 0 stays outermost like the
    // reference recursion. Rows validate lazily left-to-right stopping after the
    // first empty row: an empty leading row yields nothing without touching later
    // rows, matching short-circuiting in the reference definition.
    private static IEnumerable<JsonNode?> Combos(IList<JsonNode?> matrix, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(context);
        foreach (JsonNode? row in matrix)
        {
            context.Budget.CheckCancellation();
            if (row is not JsonArray cells)
                throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(row)}");
            if (cells.Count == 0)
                yield break;
        }
        if (matrix.Count == 0)
        {
            context.Budget.CheckCancellation();
            yield return new JsonArray();
            yield break;
        }
        context.Budget.ReserveBytes(4L * matrix.Count);
        var positions = new int[matrix.Count];
        while (true)
        {
            context.Budget.CheckCancellation();
            var combo = new JsonArray();
            context.Budget.CheckCancellation();
            for (int index = 0; index < matrix.Count; index++)
            {
                context.Budget.CheckCancellation();
                JsonNode? value = RowAt(index)[positions[index]];
                context.Budget.ReserveArrayItem(value);
                combo.Add(context.Runtime.Clone(value));
            }
            yield return combo;
            int cursor = matrix.Count - 1;
            while (cursor >= 0)
            {
                context.Budget.CheckCancellation();
                if (++positions[cursor] < RowAt(cursor).Count)
                    break;
                positions[cursor] = 0;
                cursor--;
            }
            if (cursor < 0)
                yield break;
        }

        JsonArray RowAt(int index) => matrix[index] as JsonArray
            ?? throw new InvalidOperationException("A validated combination row changed.");
    }
}

// `bsearch(target)`: binary search with total ordering; misses yield -1-ix.
internal sealed class BsearchFilter(JqFilter Target) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (input is not JsonArray sorted)
            throw new JqException($"{JqRuntime.TypeName(input)} ({context.Runtime.Serialize(input, false, null, false)}) cannot be searched from");
        foreach (JsonNode? target in Target.Evaluate(input, context, environment))
        {
            using var comparer = new JqComparer(context.Budget);
            int low = 0;
            int high = sorted.Count;
            int found = -1;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                int order = comparer.Compare(target, sorted[middle]);
                if (order == 0)
                {
                    found = middle;
                    break;
                }
                if (order < 0)
                    high = middle;
                else
                    low = middle + 1;
            }
            yield return JsonValue.Create(found >= 0 ? found : -1 - low);
        }
    }
}

// Comparison keys borrow their values without parenting or cloning them. The
// collected sequence mirrors map([f]) without adding a JSON nesting level.
internal static class CollectionKeys
{
    internal static JsonArray RequireArray(JsonNode? input, JqFilter keys, JqContext context, JqEnvironment environment, bool sorting)
    {
        if (input is JsonArray array) return array;
        // jq evaluates map([f]) before its internal array check. Retain those
        // effects and failures; only this invalid-input path needs JSON keys.
        JsonNode? mapped = new MapFilter(new ArrayFilter(keys)).Evaluate(input, context, environment).Single();
        string operation = sorting ? "cannot be sorted, as they are not both arrays" : "cannot be iterated over";
        throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false)
            + ") and array (" + context.Runtime.Serialize(mapped, false, null, false) + ") " + operation);
    }

    internal static IEnumerable<(JsonNode? Element, IReadOnlyList<JsonNode?> Keys)> Keyed(JsonNode? input, JqFilter keys, JqContext context, JqEnvironment environment)
    {
        JsonArray array = RequireArray(input, keys, context, environment, true);
        context.Budget.ReserveBytes(80L * array.Count);
        foreach (JsonNode? element in MapFilter.Iterate(input, context, environment))
        {
            var collected = new List<JsonNode?>();
            foreach (JsonNode? key in keys.Evaluate(element, context, environment))
            {
                // Reference slots and geometric backing replacement peaks.
                context.Budget.ReserveBytes(32);
                collected.Add(key);
            }
            yield return (element, collected);
        }
    }

    internal static bool Equal(IReadOnlyList<JsonNode?> left, IReadOnlyList<JsonNode?> right, JqRuntime runtime)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
            if (!runtime.JsonEquals(left[index], right[index])) return false;
        return true;
    }

}

// `sort_by(f)`: stable ordering by collected keys.
internal sealed class SortByFilter(JqFilter Keys) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var result = new JsonArray();
        context.Budget.CheckCancellation();
        using var comparer = new JqComparer(context.Budget);
        foreach (var (element, _) in JqComparer.OrderBy(CollectionKeys.Keyed(input, Keys, context, environment),
            static pair => pair.Keys, (IComparer<IReadOnlyList<JsonNode?>>)comparer))
        {
            context.Budget.CheckCancellation();
            context.Budget.ReserveArrayItem(element);
            result.Add(context.Runtime.Clone(element));
        }
        yield return result;
    }
}

// `group_by(f)`: stable-sorted runs of equal keys.
internal sealed class GroupByFilter(JqFilter Keys) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        using var comparer = new JqComparer(context.Budget);
        var ordered = JqComparer.OrderBy(CollectionKeys.Keyed(input, Keys, context, environment),
            static pair => pair.Keys, (IComparer<IReadOnlyList<JsonNode?>>)comparer);
        var result = new JsonArray();
        context.Budget.CheckCancellation();
        JsonArray? group = null;
        IReadOnlyList<JsonNode?> current = Array.Empty<JsonNode?>();
        foreach (var (element, keys) in ordered)
        {
            if (group is null || !CollectionKeys.Equal(current, keys, context.Runtime))
            {
                group = new JsonArray();
                context.Budget.CheckCancellation();
                result.Add(group);
                current = keys;
            }
            context.Budget.CheckCancellation();
            context.Budget.ReserveArrayItem(element);
            group.Add(context.Runtime.Clone(element));
        }
        yield return result;
    }
}

// `unique_by(f)`: first element per equal-key run in sorted order.
internal sealed class UniqueByFilter(JqFilter Keys) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        using var comparer = new JqComparer(context.Budget);
        var ordered = JqComparer.OrderBy(CollectionKeys.Keyed(input, Keys, context, environment),
            static pair => pair.Keys, (IComparer<IReadOnlyList<JsonNode?>>)comparer);
        var result = new JsonArray();
        context.Budget.CheckCancellation();
        IReadOnlyList<JsonNode?> current = Array.Empty<JsonNode?>();
        bool first = true;
        foreach (var (element, keys) in ordered)
        {
            if (first || !CollectionKeys.Equal(current, keys, context.Runtime))
            {
                context.Budget.CheckCancellation();
                context.Budget.ReserveArrayItem(element);
                result.Add(context.Runtime.Clone(element));
                current = keys;
                first = false;
            }
        }
        yield return result;
    }
}

// `min_by(f)` keeps the first minimum; `max_by(f)` keeps the last maximum.
internal sealed class MinMaxByFilter(JqFilter Keys, bool TakeMax) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        input = CollectionKeys.RequireArray(input, Keys, context, environment, false);
        JsonNode? best = null;
        IReadOnlyList<JsonNode?> bestKeys = Array.Empty<JsonNode?>();
        bool empty = true;
        using var comparer = new JqComparer(context.Budget);
        foreach (var (element, keys) in CollectionKeys.Keyed(input, Keys, context, environment))
        {
            int order = empty ? 0 : comparer.CompareKeys(keys, bestKeys);
            if (empty || (TakeMax ? order >= 0 : order < 0))
            {
                best = element;
                bestKeys = keys;
                empty = false;
            }
        }
        yield return empty ? null : best;
    }
}

// _sort_by_impl(keys): stable ordering by precomputed key arrays.
internal sealed class SortByImplFilter(JqFilter Keys) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? keysValue in Keys.Evaluate(input, context, environment))
        {
            if (input is not JsonArray elements || keysValue is not JsonArray keys || elements.Count != keys.Count)
                throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") and " + TypeName(keysValue) + " (" + context.Runtime.Serialize(keysValue, false, null, false) + ") cannot be sorted, as they are not both arrays");
            context.Budget.ReserveBytes(80L * elements.Count);
            var pairs = new List<(JsonNode? Element, JsonNode? Key)>();
            for (int index = 0; index < elements.Count; index++)
            {
                context.Budget.CheckCancellation();
                pairs.Add((elements[index], keys[index]));
            }
            var result = new JsonArray();
            context.Budget.CheckCancellation();
            using var comparer = new JqComparer(context.Budget);
            foreach (var pair in JqComparer.OrderBy(pairs, static item => item.Key, (IComparer<JsonNode?>)comparer))
            {
                context.Budget.CheckCancellation();
                context.Budget.ReserveArrayItem(pair.Element);
                result.Add(context.Runtime.Clone(pair.Element));
            }
            yield return result;
        }
    }
}

// _group_by_impl(keys): stable-sorted runs of equal precomputed keys.
internal sealed class GroupByImplFilter(JqFilter Keys) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? keysValue in Keys.Evaluate(input, context, environment))
        {
            if (input is not JsonArray elements || keysValue is not JsonArray keys || elements.Count != keys.Count)
                throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") and " + TypeName(keysValue) + " (" + context.Runtime.Serialize(keysValue, false, null, false) + ") cannot be sorted, as they are not both arrays");
            context.Budget.ReserveBytes(80L * elements.Count);
            var pairs = new List<(JsonNode? Element, JsonNode? Key)>();
            for (int index = 0; index < elements.Count; index++)
            {
                context.Budget.CheckCancellation();
                pairs.Add((elements[index], keys[index]));
            }
            using var comparer = new JqComparer(context.Budget);
            var ordered = JqComparer.OrderBy(pairs, static item => item.Key, (IComparer<JsonNode?>)comparer);
            var result = new JsonArray();
            context.Budget.CheckCancellation();
            JsonArray? group = null;
            JsonNode? current = null;
            foreach (var pair in ordered)
            {
                if (group is null || !context.Runtime.JsonEquals(current, pair.Key))
                {
                    group = new JsonArray();
                    context.Budget.CheckCancellation();
                    result.Add(group);
                    current = pair.Key;
                }
                context.Budget.CheckCancellation();
                context.Budget.ReserveArrayItem(pair.Element);
                group.Add(context.Runtime.Clone(pair.Element));
            }
            yield return result;
        }
    }
}

// _unique_by_impl(keys): first element per equal-key run in sorted order.
internal sealed class UniqueByImplFilter(JqFilter Keys) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? keysValue in Keys.Evaluate(input, context, environment))
        {
            if (input is not JsonArray elements || keysValue is not JsonArray keys || elements.Count != keys.Count)
                throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") and " + TypeName(keysValue) + " (" + context.Runtime.Serialize(keysValue, false, null, false) + ") cannot be sorted, as they are not both arrays");
            context.Budget.ReserveBytes(80L * elements.Count);
            var pairs = new List<(JsonNode? Element, JsonNode? Key)>();
            for (int index = 0; index < elements.Count; index++)
            {
                context.Budget.CheckCancellation();
                pairs.Add((elements[index], keys[index]));
            }
            using var comparer = new JqComparer(context.Budget);
            var ordered = JqComparer.OrderBy(pairs, static item => item.Key, (IComparer<JsonNode?>)comparer);
            var result = new JsonArray();
            context.Budget.CheckCancellation();
            JsonNode? current = null;
            bool first = true;
            foreach (var pair in ordered)
            {
                if (first || !context.Runtime.JsonEquals(current, pair.Key))
                {
                    context.Budget.CheckCancellation();
                    context.Budget.ReserveArrayItem(pair.Element);
                    result.Add(context.Runtime.Clone(pair.Element));
                    current = pair.Key;
                    first = false;
                }
            }
            yield return result;
        }
    }
}

// _min_by_impl and _max_by_impl with precomputed keys: first extreme wins for min, last wins for max; empty yields null.
internal sealed class MinMaxByImplFilter(JqFilter Keys, bool TakeMax) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? keysValue in Keys.Evaluate(input, context, environment))
        {
            if (input is not JsonArray elements || keysValue is not JsonArray keys)
                throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") and " + TypeName(keysValue) + " (" + context.Runtime.Serialize(keysValue, false, null, false) + ") cannot be iterated over");
            if (elements.Count != keys.Count)
                throw new JqRuntimeException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") and " + TypeName(keysValue) + " (" + context.Runtime.Serialize(keysValue, false, null, false) + ") have wrong length");
            if (elements.Count == 0)
            {
                yield return null;
                continue;
            }
            using var comparer = new JqComparer(context.Budget);
            JsonNode? best = elements[0];
            JsonNode? bestKey = keys[0];
            for (int index = 1; index < elements.Count; index++)
            {
                context.Budget.CheckCancellation();
                int order = comparer.Compare(keys[index], bestKey);
                if (TakeMax ? order >= 0 : order < 0)
                {
                    best = elements[index];
                    bestKey = keys[index];
                }
            }
            yield return best;
        }
    }
}

// _flatten(depth): depth-limited flattening without the negative-depth guard.
internal sealed class FlattenImplFilter(JqFilter Depth) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? depth in Depth.Evaluate(input, context, environment))
        {
            // _flatten carries no negative guard upstream; numeric depths
            // recurse by decrementing while non-numeric depths recurse
            // through depth - 1 and fail staged at the first nested array.
            yield return context.Runtime.Flatten(input, depth);
        }
    }
}

// INDEX(stream; idx_expr) and INDEX(idx_expr): build an object keyed by tostring per stream row.
internal sealed class SqlIndexFilter(JqFilter? Stream, JqFilter IndexExpr) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        IEnumerable<JsonNode?> rows = Stream is null ? MapFilter.Iterate(input, context, environment) : Stream.Evaluate(input, context, environment);
        var result = new JsonObject();
        context.Budget.CheckCancellation();
        foreach (JsonNode? row in rows)
        {
            foreach (JsonNode? key in IndexExpr.Evaluate(row, context, environment))
            {
                string name = context.Runtime.ToJqString(key);
                context.Budget.CheckCancellation();
                context.Budget.ReserveString(name.Length);
                context.Budget.ReserveObjectItem(row);
                result[name] = context.Runtime.Clone(row);
            }
        }
        yield return result;
    }
}

// JOIN(idx; stream; idx_expr; join_expr) family: pair stream rows with index lookups.
internal sealed class SqlJoinFilter(JqFilter Index, JqFilter? Stream, JqFilter IndexExpr, JqFilter? JoinExpr) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? idx in Index.Evaluate(input, context, environment))
        {
            if (Stream is null)
            {
                context.Budget.ReserveBytes(64);
                var collected = new JsonArray();
                context.Budget.CheckCancellation();
                foreach (JsonNode? row in MapFilter.Iterate(input, context, environment))
                {
                    context.Budget.ReserveBytes(24);
                    collected.Add(CreatePair(idx, row, 1));
                }
                yield return collected;
            }
            else
            {
                foreach (JsonNode? row in Stream.Evaluate(input, context, environment))
                {
                    JsonArray pair = CreatePair(idx, row, 0);
                    if (JoinExpr is null)
                    {
                        yield return pair;
                    }
                    else
                    {
                        foreach (JsonNode? joined in JoinExpr.Evaluate(pair, context, environment))
                            yield return joined;
                    }
                }
            }
        }

        JsonArray CreatePair(JsonNode? idx, JsonNode? row, int depth)
        {
            context.Budget.ReserveBytes(88 + (row is JsonValue ? 64 : 0));
            var pair = new JsonArray
            {
                context.Budget.CopyValue(row, depth + 1)
            };
            foreach (JsonNode? key in IndexExpr.Evaluate(row, context, environment))
            {
                JsonNode? lookup = LookupIndex(idx, key);
                context.Budget.ReserveArrayItem(lookup);
                pair.Add(context.Budget.CopyValue(lookup, depth + 1));
            }
            return pair;
        }
    }

    private static JsonNode? LookupIndex(JsonNode? idx, JsonNode? key)
    {
        if (idx is null)
            return null;
        if (idx is JsonObject obj && TryGetString(key, out string? name) && name is not null)
            return obj.TryGetPropertyValue(name, out JsonNode? child) ? child : null;
        if (idx is JsonArray arr && JqPaths.TryGetIndex(key, out long index, out bool isNaN))
        {
            if (isNaN)
                return null;
            long resolved = index < 0 ? arr.Count + index : index;
            return resolved >= 0 && resolved < arr.Count ? arr[(int)resolved] : null;
        }
        throw new JqRuntimeException("cannot index " + TypeName(idx));
    }
}

// `any(generator; condition)`: true on the first truthy condition output,
// short-circuiting the rest. Exhaustion yields false; errors propagate.
internal sealed class AnyFilter(JqFilter Generator, JqFilter Condition) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        yield return JsonValue.Create(JqPredicateFold.Evaluate(Generator, Condition, input, context, environment, false));
    }
}

// `all(generator; condition)`: false on the first falsy condition output,
// short-circuiting the rest. Exhaustion yields true; errors propagate.
internal sealed class AllFilter(JqFilter Generator, JqFilter Condition) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        yield return JsonValue.Create(JqPredicateFold.Evaluate(Generator, Condition, input, context, environment, true));
    }
}

// Only a boolean escapes a completed predicate. Source allocations precede its
// checkpoint; its temporary demand ends after the enumerator is disposed.
internal static class JqPredicateFold
{
    internal static bool Evaluate(JqFilter generator, JqFilter condition, JsonNode? input,
        JqContext context, JqEnvironment environment, bool all)
    {
        foreach (JsonNode? generated in generator.Evaluate(input, context, environment))
        {
            JqStorage? storage = context.Budget.TemporaryStorage;
            long before = storage?.UsedBytes ?? 0;
            bool decided = false;
            using (var probes = condition.Evaluate(generated, context, environment).GetEnumerator())
            {
                while (probes.MoveNext())
                {
                    if (Truthy(probes.Current) == all) continue;
                    decided = true;
                    break;
                }
            }
            // Failure, including enumerator disposal, bypasses reconciliation:
            // its payload may escape to a handler under the existing owner.
            if (storage is not null) storage.DiscardBytes(storage.UsedBytes - before);
            if (decided) return !all;
        }
        return all;
    }
}
