using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// `map(f)`: collects every f-output per element into a new array.
internal sealed class MapFilter(JqFilter Body) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var result = new JsonArray();
        context.Budget.CheckCancellation();
        foreach (JsonNode? element in IteratorFilter.ReadValues(input, false, context.Budget))
            await foreach (JsonNode? value in Body.Evaluate(element, context, environment).ConfigureAwait(false))
            {
                context.Budget.ReserveArrayItem(value);
                result.Add(context.Budget.CopyValue(value, 1));
            }
        yield return result;
    }

}

// `map_values(f)`: first f-output per element, dropping empties. Objects
// keep their keys; anything else follows iteration errors.
internal sealed class MapValuesFilter(JqFilter Body) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (input is JsonArray array)
        {
            var result = new JsonArray();
            context.Budget.CheckCancellation();
            foreach (JsonNode? element in array)
            {
                var outputs = Body.Evaluate(element, context, environment).GetAsyncEnumerator();
                await using var outputsLifetime = outputs.ConfigureAwait(false);
                if (await outputs.MoveNextAsync().ConfigureAwait(false))
                {
                    context.Budget.ReserveArrayItem(outputs.Current);
                    result.Add(context.Budget.CopyValue(outputs.Current, 1));
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
                var outputs = Body.Evaluate(property.Value, context, environment).GetAsyncEnumerator();
                await using var outputsLifetime = outputs.ConfigureAwait(false);
                if (await outputs.MoveNextAsync().ConfigureAwait(false))
                {
                    context.Budget.ReserveObjectItem(outputs.Current);
                    result.Add(property.Key, context.Budget.CopyValue(outputs.Current, 1));
                }
            }
            yield return result;
            yield break;
        }
        throw new JqException($"cannot iterate over {TypeName(input)}");
    }
}

// `with_entries(f)`: entries through f and back, multi-outputs included.
internal sealed class WithEntriesFilter(JqFilter Body) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        context.Budget.ReserveBytes(64);
        var mapped = new List<JsonNode?>();
        foreach (JsonNode? entry in context.Runtime.ToEntries(input))
            await foreach (JsonNode? value in Body.Evaluate(entry, context, environment).ConfigureAwait(false))
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
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? container in Containers.Evaluate(input, context, environment).ConfigureAwait(false))
            yield return JsonValue.Create(context.Runtime.Has(container, input));
    }
}

// `add` and `add(f)`: folds a value stream with `+` from null.
internal sealed class AddValuesFilter(JqFilter Values) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        yield return await context.Runtime.AddValues(Values.Evaluate(input, context, environment)).ConfigureAwait(false);
    }
}

// `flatten` and `flatten(depth)`: depth-limited flattening over arrays.
// Like the reference desugar, the negative guard compares with jq ordering:
// null, booleans, and NaN (which sorts as null) fail it, while strings,
// arrays, and objects sort above zero and recurse through depth - 1.
internal sealed class FlattenFilter(JqFilter? Depth) : JqFilter
{

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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (Depth is null)
        {
            yield return context.Runtime.Flatten(input, JsonValue.Create(-1));
            yield break;
        }
        await foreach (JsonNode? depth in Depth.Evaluate(input, context, environment).ConfigureAwait(false))
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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        int height = input is JsonArray array ? array.Count : input is JsonObject fields ? fields.Count
            : throw new JqException($"cannot iterate over {JqRuntime.TypeName(input)}");
        double width = double.NaN;
        JsonNode? invalidRow = null;
        foreach (JsonNode? row in Rows())
        {
            context.Budget.CheckCancellation();
            double length;
            if (row is JsonArray present) length = present.Count;
            else if (row is null) length = 0;
            else
            {
                length = Number(context.Runtime.Length(row));
                invalidRow ??= row;
            }
            width = double.MaxNumber(width, length);
            context.Budget.CheckCancellation();
        }
        // Zero-width rows need no indexing. Finish length evaluation before
        // requesting the first column, including for huge or non-finite widths.
        if (invalidRow is not null && (width > 0 || double.IsNaN(width)))
            _ = JqPathReads.ReadIndex(invalidRow, 0, false, context);
        int columns = width > 0 ? (int)width : 0;
        context.Budget.ReserveBytes(64 + 24L * columns);
        var result = new JsonArray();
        for (int column = 0; column < columns; column++)
        {
            context.Budget.ReserveBytes(64 + 24L * height);
            var line = new JsonArray();
            foreach (JsonNode? row in Rows())
            {
                context.Budget.CheckCancellation();
                JsonNode? cell = JqPathReads.ReadIndex(row, column, false, context);
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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var current = new JsonNode?[args.Count];
        await foreach (JsonNode? value in Generate(0).ConfigureAwait(false)) yield return value;

        async IAsyncEnumerable<JsonNode?> Generate(int index)
        {
            if (index >= args.Count)
            {
                JsonNode? startValue = args.Count == 1 ? JsonValue.Create(0) : current[0];
                JsonNode? endValue = current[args.Count == 1 ? 0 : 1];
                if (args.Count < 3 || (TypeName(startValue) == "number" && TypeName(endValue) == "number" && TypeName(current[2]) == "number"))
                {
                    double start = Bound(startValue);
                    double end = Bound(endValue);
                    double step = args.Count == 3 ? Number(current[2]) : 1;
                    bool positive = step > 0;
                    if (!positive && !(step < 0) && !double.IsNaN(step)) yield break;
                    for (double value = start; positive ? value < end : value > end; value += step)
                    {
                        context.Budget.CheckCancellation();
                        yield return CreateNumber(value);
                    }
                }
                else
                {
                    JsonNode? step = current[2];
                    int direction = context.Runtime.Compare(step, JsonValue.Create(0));
                    if (direction == 0) yield break;
                    for (JsonNode? value = startValue;
                        direction > 0 ? context.Runtime.Compare(value, endValue) < 0 : context.Runtime.Compare(value, endValue) > 0;
                        value = context.Runtime.Add(value, step))
                    {
                        context.Budget.CheckCancellation();
                        yield return value;
                    }
                }
                yield break;
            }
            await foreach (JsonNode? value in args[index].Evaluate(input, context, environment).ConfigureAwait(false))
            {
                current[index] = value;
                await foreach (JsonNode? result in Generate(index + 1).ConfigureAwait(false)) yield return result;
            }
        }

        static double Bound(JsonNode? value)
        {
            if (TypeName(value) != "number") throw new JqException("Range bounds must be numeric");
            return Number(value);
        }
    }
}

// `combinations` and `combinations(n)`: cartesian products of array rows.
internal sealed class CombinationsFilter(JqFilter? Count) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (Count is null)
        {
            // Like the reference length check, length-zero inputs yield
            // one empty combination instead of iterating.
            if (JqRuntime.Number(context.Runtime.Length(input)) == 0)
            {
                context.Budget.ReserveBytes(64);
                yield return new JsonArray();
                yield break;
            }
            if (input is not JsonArray matrix)
                throw new JqException($"cannot iterate over {JqRuntime.TypeName(input)}");
            foreach (JsonNode? combo in Combos(matrix, null, context))
                yield return combo;
            yield break;
        }
        double repetitions = 0;
        await foreach (JsonNode? count in Count.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (TypeName(count) != "number")
                throw new JqException("Range bounds must be numeric");
            double level = Number(count);
            // [range(n) | $dot] collects all count results before the product.
            // Retain their total rather than a matrix of identical references.
            if (level > 0) repetitions += Math.Ceiling(level);
        }
        context.Budget.CheckCancellation();
        if (repetitions == 0)
        {
            context.Budget.ReserveBytes(64);
            yield return new JsonArray();
            yield break;
        }
        if (input is not JsonArray row)
            throw new JqException($"cannot iterate over {JqRuntime.TypeName(input)}");
        if (row.Count == 0) yield break;
        if (repetitions > Array.MaxLength)
            throw new JqQuotaException("array cannot be represented");
        foreach (JsonNode? combo in Combos(row, (int)repetitions, context))
            yield return combo;
    }

    // Iterative odometer over row positions. Recursion depth here would be input
    // length, which must not translate into unbounded CLR stack depth, so
    // nested enumerator frames are not an option. Row 0 stays outermost like the
    // reference recursion. Rows validate lazily left-to-right stopping after the
    // first empty row: an empty leading row yields nothing without touching later
    // rows, matching short-circuiting in the reference definition.
    private static IEnumerable<JsonNode?> Combos(JsonArray matrix, int? repetitions, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(context);
        if (repetitions is null)
        {
            foreach (JsonNode? row in matrix)
            {
                context.Budget.CheckCancellation();
                if (row is not JsonArray cells)
                    throw new JqException($"cannot iterate over {JqRuntime.TypeName(row)}");
                if (cells.Count == 0)
                    yield break;
            }
        }
        int count = repetitions ?? matrix.Count;
        context.Budget.ReserveBytes(24 + 4L * count);
        var positions = new int[count];
        while (true)
        {
            context.Budget.ReserveBytes(64);
            var combo = new JsonArray();
            for (int index = 0; index < count; index++)
            {
                context.Budget.CheckCancellation();
                JsonNode? value = RowAt(index)[positions[index]];
                context.Budget.ReserveArrayItem(value);
                combo.Add(context.Budget.CopyValue(value, 1));
            }
            yield return combo;
            int cursor = count - 1;
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

        JsonArray RowAt(int index) => repetitions.HasValue ? matrix : matrix[index] as JsonArray
            ?? throw new InvalidOperationException("A validated combination row changed.");
    }
}

// `bsearch(target)`: binary search with total ordering; misses yield -1-ix.
internal sealed class BsearchFilter(JqFilter Target) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? target in Target.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (input is not JsonArray sorted)
                throw new JqException($"{JqRuntime.TypeName(input)} ({context.Runtime.TruncatedDump(input)}) cannot be searched from");
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
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<JsonArray> RequireArray(JsonNode? input, JqFilter keys, JqContext context, JqEnvironment environment, bool sorting)
    {
        if (input is JsonArray array) return array;
        // jq evaluates map([f]) before its internal array check. Retain those
        // effects and failures; only this invalid-input path needs JSON keys.
        JsonNode? mapped = await new MapFilter(new ArrayFilter(keys)).Evaluate(input, context, environment).SingleAsync().ConfigureAwait(false);
        string operation = sorting ? "cannot be sorted, as they are not both arrays" : "cannot be iterated over";
        throw new JqException(TypeName(input) + " (" + context.Runtime.TruncatedDump(input)
            + ") and array (" + context.Runtime.TruncatedDump(mapped) + ") " + operation);
    }

    internal static async IAsyncEnumerable<(JsonNode? Element, IReadOnlyList<JsonNode?> Keys)> Keyed(JsonNode? input, JqFilter keys, JqContext context, JqEnvironment environment)
    {
        JsonArray array = await RequireArray(input, keys, context, environment, true).ConfigureAwait(false);
        context.Budget.ReserveBytes(80L * array.Count);
        foreach (JsonNode? element in IteratorFilter.ReadValues(input, false, context.Budget))
        {
            var collected = new List<JsonNode?>();
            await foreach (JsonNode? key in keys.Evaluate(element, context, environment).ConfigureAwait(false))
            {
                // Reference slots and geometric backing replacement peaks.
                context.Budget.ReserveBytes(32);
                collected.Add(key);
            }
            yield return (element, collected);
        }
    }

    internal static IEnumerable<(JsonNode? Element, JsonNode? Key)> OrderPrecomputed(JsonNode? input, JsonNode? keysValue, JqContext context)
    {
        if (input is not JsonArray elements || keysValue is not JsonArray keys || elements.Count != keys.Count)
            throw new JqException(TypeName(input) + " (" + context.Runtime.TruncatedDump(input) + ") and "
                + TypeName(keysValue) + " (" + context.Runtime.TruncatedDump(keysValue) + ") cannot be sorted, as they are not both arrays");
        context.Budget.ReserveBytes(32L * elements.Count);
        return Ordered();

        IEnumerable<(JsonNode? Element, JsonNode? Key)> Ordered()
        {
            using var comparer = new JqComparer(context.Budget);
            foreach (int index in JqComparer.OrderBy(Enumerable.Range(0, elements.Count), index => keys[index], (IComparer<JsonNode?>)comparer))
            {
                context.Budget.CheckCancellation();
                yield return (elements[index], keys[index]);
            }
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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var result = new JsonArray();
        context.Budget.CheckCancellation();
        using var comparer = new JqComparer(context.Budget);
        await foreach (var (element, _) in JqComparer.OrderBy(CollectionKeys.Keyed(input, Keys, context, environment),
            static pair => pair.Keys, (IComparer<IReadOnlyList<JsonNode?>>)comparer).ConfigureAwait(false))
        {
            context.Budget.CheckCancellation();
            context.Budget.ReserveArrayItem(element);
            result.Add(context.Budget.CopyValue(element, 1));
        }
        yield return result;
    }
}

// `group_by(f)`: stable-sorted runs of equal keys.
internal sealed class GroupByFilter(JqFilter Keys) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
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
        await foreach (var (element, keys) in ordered.ConfigureAwait(false))
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
            group.Add(context.Budget.CopyValue(element, 2));
        }
        yield return result;
    }
}

// `unique_by(f)`: first element per equal-key run in sorted order.
internal sealed class UniqueByFilter(JqFilter Keys) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
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
        await foreach (var (element, keys) in ordered.ConfigureAwait(false))
        {
            if (first || !CollectionKeys.Equal(current, keys, context.Runtime))
            {
                context.Budget.CheckCancellation();
                context.Budget.ReserveArrayItem(element);
                result.Add(context.Budget.CopyValue(element, 1));
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
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JsonArray array = await CollectionKeys.RequireArray(input, Keys, context, environment, false).ConfigureAwait(false);
        if (array.Count == 0)
        {
            yield return null;
            yield break;
        }
        using var memory = new JqMemoryReservation(context.Budget.MemoryBudget);
        memory.Resize(128);
        context.Budget.CheckCancellation();
        var current = new List<JsonNode?>();
        var bestKeys = new List<JsonNode?>();
        JsonNode? best = null;
        bool empty = true;
        using var comparer = new JqComparer(context.Budget);
        foreach (JsonNode? element in array)
        {
            current.Clear();
            await foreach (JsonNode? key in Keys.Evaluate(element, context, environment).ConfigureAwait(false))
            {
                context.Budget.CheckCancellation();
                if (current.Count == current.Capacity)
                {
                    int previous = current.Capacity;
                    int capacity = JqBudget.BufferCapacity((long)current.Count + 1, Math.Max(4, 2L * previous));
                    long retained = memory.LastAdmittedBytes;
                    memory.Resize(checked(retained + 8L * capacity));
                    context.Budget.CheckCancellation();
                    current.Capacity = capacity;
                    if (previous != 0) memory.Resize(retained + 8L * (capacity - previous));
                }
                current.Add(key);
            }
            context.Budget.CheckCancellation();
            int order = empty ? 0 : comparer.CompareKeys(current, bestKeys);
            if (empty || (TakeMax ? order >= 0 : order < 0))
            {
                best = element;
                (current, bestKeys) = (bestKeys, current);
                empty = false;
            }
        }
        yield return best;
    }
}

// _sort_by_impl(keys): stable ordering by precomputed key arrays.
internal sealed class SortByImplFilter(JqFilter Keys) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? keysValue in Keys.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            var ordered = CollectionKeys.OrderPrecomputed(input, keysValue, context);
            context.Budget.ReserveBytes(64);
            var result = new JsonArray();
            context.Budget.CheckCancellation();
            foreach (var pair in ordered)
            {
                context.Budget.CheckCancellation();
                context.Budget.ReserveArrayItem(pair.Element);
                result.Add(context.Budget.CopyValue(pair.Element, 1));
            }
            yield return result;
        }
    }
}

// _group_by_impl(keys): stable-sorted runs of equal precomputed keys.
internal sealed class GroupByImplFilter(JqFilter Keys) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? keysValue in Keys.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            var ordered = CollectionKeys.OrderPrecomputed(input, keysValue, context);
            context.Budget.ReserveBytes(64);
            var result = new JsonArray();
            context.Budget.CheckCancellation();
            JsonArray? group = null;
            JsonNode? current = null;
            foreach (var pair in ordered)
            {
                if (group is null || !context.Runtime.JsonEquals(current, pair.Key))
                {
                    context.Budget.ReserveBytes(88);
                    group = new JsonArray();
                    context.Budget.CheckCancellation();
                    result.Add(group);
                    current = pair.Key;
                }
                context.Budget.CheckCancellation();
                context.Budget.ReserveArrayItem(pair.Element);
                group.Add(context.Budget.CopyValue(pair.Element, 2));
            }
            yield return result;
        }
    }
}

// _unique_by_impl(keys): first element per equal-key run in sorted order.
internal sealed class UniqueByImplFilter(JqFilter Keys) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? keysValue in Keys.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            var ordered = CollectionKeys.OrderPrecomputed(input, keysValue, context);
            context.Budget.ReserveBytes(64);
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
                    result.Add(context.Budget.CopyValue(pair.Element, 1));
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
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? keysValue in Keys.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (input is not JsonArray elements || keysValue is not JsonArray keys)
                throw new JqException(TypeName(input) + " (" + context.Runtime.TruncatedDump(input) + ") and " + TypeName(keysValue) + " (" + context.Runtime.TruncatedDump(keysValue) + ") cannot be iterated over");
            if (elements.Count != keys.Count)
                throw new JqException(TypeName(input) + " (" + context.Runtime.TruncatedDump(input) + ") and " + TypeName(keysValue) + " (" + context.Runtime.TruncatedDump(keysValue) + ") have wrong length");
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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? depth in Depth.Evaluate(input, context, environment).ConfigureAwait(false))
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

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        IAsyncEnumerable<JsonNode?> rows = Stream is null ? IteratorFilter.ReadValues(input, false, context.Budget).ToAsyncEnumerable() : Stream.Evaluate(input, context, environment);
        context.Budget.ReserveBytes(64);
        var result = new JsonObject();
        await foreach (JsonNode? row in rows.ConfigureAwait(false))
        {
            if (IndexExpr is SingleValueFilter single)
                Add(row, single.ReadValue(row, context, environment));
            else
            {
                await foreach (JsonNode? key in IndexExpr.Evaluate(row, context, environment).ConfigureAwait(false))
                    Add(row, key);
            }
        }
        yield return result;

        void Add(JsonNode? row, JsonNode? key)
        {
            // Finite scalar formatting fits 32 UTF-16 units. Strings are
            // borrowed; serialized keys already admit their own storage.
            if (TypeName(key) == "number" && double.IsFinite(Number(key)))
                context.Budget.ReserveString(32);
            string name = context.Runtime.ToJqString(key);
            context.Budget.ReserveObjectItem(row);
            result[name] = context.Budget.CopyValue(row, 1);
        }
    }
}

// JOIN(idx; stream; idx_expr; join_expr) family: pair stream rows with index lookups.
internal sealed class SqlJoinFilter(JqFilter Index, JqFilter? Stream, JqFilter IndexExpr, JqFilter? JoinExpr) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var result in Run(input, context, environment, null).ConfigureAwait(false))
            yield return result.Value;
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath outer, JqContext context, JqEnvironment environment)
    {
        await foreach (var result in Run(outer.Value, context, environment, outer).ConfigureAwait(false))
            yield return result.Path ?? throw new InvalidOperationException("JOIN path evaluation must retain its tracking result.");
    }

    private async IAsyncEnumerable<(JsonNode? Value, JqValuePath? Path)> Run(JsonNode? input, JqContext context, JqEnvironment environment, JqValuePath? path)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? idx in Index.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (Stream is null)
            {
                context.Budget.ReserveBytes(64);
                var collected = new JsonArray();
                context.Budget.CheckCancellation();
                await foreach (var (row, rowPath) in Rows().ConfigureAwait(false))
                {
                    context.Budget.ReserveBytes(24);
                    collected.Add(await CreatePair(idx, row, rowPath, 1).ConfigureAwait(false));
                }
                yield return (collected, path?.WithValue(collected, false));
            }
            else
            {
                await foreach (var (row, rowPath) in Rows().ConfigureAwait(false))
                {
                    JsonArray pair = await CreatePair(idx, row, rowPath, 0).ConfigureAwait(false);
                    if (JoinExpr is null)
                    {
                        yield return (pair, rowPath?.WithValue(pair, false));
                    }
                    else if (rowPath is null)
                    {
                        await foreach (JsonNode? joined in JoinExpr.Evaluate(pair, context, environment).ConfigureAwait(false))
                            yield return (joined, null);
                    }
                    else
                    {
                        JqValuePath untracked = rowPath.WithValue(pair, false);
                        await foreach (JqValuePath joined in JoinExpr.EvaluatePaths(untracked, context, environment).ConfigureAwait(false))
                            yield return (joined.Value, joined);
                    }
                }
            }
        }

        async IAsyncEnumerable<(JsonNode? Value, JqValuePath? Path)> Rows()
        {
            if (path is null)
            {
                await foreach (JsonNode? row in (Stream is null ? IteratorFilter.ReadValues(input, false, context.Budget).ToAsyncEnumerable() : Stream.Evaluate(input, context, environment)).ConfigureAwait(false))
                    yield return (row, null);
            }
            else
            {
                await foreach (JqValuePath row in (Stream is null ? IteratorFilter.ReadPaths(path, context).ToAsyncEnumerable() : Stream.EvaluatePaths(path, context, environment)).ConfigureAwait(false))
                    yield return (row.Value, row);
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        async ValueTask<JsonArray> CreatePair(JsonNode? idx, JsonNode? row, JqValuePath? rowPath, int depth)
        {
            context.Budget.ReserveBytes(88 + (row is JsonValue ? 64 : 0));
            var pair = new JsonArray
            {
                context.Budget.CopyValue(row, depth + 1)
            };
            await foreach (JsonNode? key in IndexExpr.Evaluate(row, context, environment).ConfigureAwait(false))
            {
                if (rowPath is not null && !TrackResult(rowPath, idx).Tracked)
                    throw new JqException(JqPaths.InvalidAccess(key, idx, context));
                _ = JqPathReads.TryIndex(idx, key, false, context, out JsonNode? lookup);
                context.Budget.ReserveArrayItem(lookup);
                pair.Add(context.Budget.CopyValue(lookup, depth + 1));
            }
            return pair;
        }
    }

}

// `any(generator; condition)`: true on the first truthy condition output,
// short-circuiting the rest. Exhaustion yields false; errors propagate.
internal sealed class AnyFilter(JqFilter Generator, JqFilter Condition) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        yield return JsonValue.Create(await JqPredicateFold.Evaluate(Generator, Condition, input, context, environment, false).ConfigureAwait(false));
    }
}

// `all(generator; condition)`: false on the first falsy condition output,
// short-circuiting the rest. Exhaustion yields true; errors propagate.
internal sealed class AllFilter(JqFilter Generator, JqFilter Condition) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        yield return JsonValue.Create(await JqPredicateFold.Evaluate(Generator, Condition, input, context, environment, true).ConfigureAwait(false));
    }
}

// Only a boolean escapes a completed predicate. Source allocations precede its
// checkpoint; its temporary demand ends after the enumerator is disposed.
internal static class JqPredicateFold
{
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<bool> Evaluate(JqFilter generator, JqFilter condition, JsonNode? input,
        JqContext context, JqEnvironment environment, bool all)
    {
        await foreach (JsonNode? generated in generator.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            JqStorage? storage = context.Budget.TemporaryStorage;
            long before = storage?.UsedBytes ?? 0;
            bool decided = false;
            await using (var probes = condition.Evaluate(generated, context, environment).ConfigureAwait(false).GetAsyncEnumerator())
            {
                while (await probes.MoveNextAsync())
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
