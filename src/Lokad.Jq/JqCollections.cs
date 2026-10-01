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
        context.Budget.ChargeNode();
        foreach (JsonNode? element in Iterate(input, context, environment))
            foreach (JsonNode? value in Body.Evaluate(element, context, environment))
                result.Add(context.Runtime.Clone(value));
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
        if (input is null)
        {
            yield return null;
            yield break;
        }
        if (input is JsonArray array)
        {
            var result = new JsonArray();
            context.Budget.ChargeNode();
            foreach (JsonNode? element in array)
            {
                using IEnumerator<JsonNode?> outputs = Body.Evaluate(element, context, environment).GetEnumerator();
                if (outputs.MoveNext())
                    result.Add(context.Runtime.Clone(outputs.Current));
            }
            yield return result;
            yield break;
        }
        if (input is JsonObject obj)
        {
            var result = new JsonObject();
            context.Budget.ChargeNode();
            foreach (var property in obj)
            {
                using IEnumerator<JsonNode?> outputs = Body.Evaluate(property.Value, context, environment).GetEnumerator();
                if (outputs.MoveNext())
                    result.Add(property.Key, context.Runtime.Clone(outputs.Current));
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
        var mapped = new JsonArray();
        foreach (JsonNode? entry in context.Runtime.ToEntries(input))
            foreach (JsonNode? value in Body.Evaluate(entry, context, environment))
                mapped.Add(context.Runtime.Clone(value));
        yield return context.Runtime.FromEntries(mapped);
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
internal sealed class FlattenFilter(JqFilter? Depth) : JqFilter
{
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (Depth is null)
        {
            yield return context.Runtime.Flatten(input, -1);
            yield break;
        }
        foreach (JsonNode? depth in Depth.Evaluate(input, context, environment))
        {
            double level = Number(depth);
            if (level < 0)
                throw new JqException("flatten depth must not be negative");
            yield return context.Runtime.Flatten(input, level);
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
        if (input is null)
        {
            context.Budget.ChargeNode();
            yield return new JsonArray();
            yield break;
        }
        var matrix = new List<JsonNode?>();
        if (input is JsonObject fields)
        {
            foreach (var property in fields)
                matrix.Add(property.Value);
        }
        else if (input is JsonArray rows)
        {
            foreach (JsonNode? row in rows)
                matrix.Add(row);
        }
        else
        {
            throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(input)}");
        }
        int width = 0;
        foreach (JsonNode? row in matrix)
        {
            if (row is not JsonArray && row is not null)
                throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(row)}");
            int length = row is JsonArray present ? present.Count : 0;
            if (length > width)
                width = length;
            context.Budget.ChargeNode();
        }
        var result = new JsonArray();
        context.Budget.ChargeNode();
        for (int column = 0; column < width; column++)
        {
            var line = new JsonArray();
            context.Budget.ChargeNode();
            foreach (JsonNode? row in matrix)
                line.Add(row is JsonArray cells && column < cells.Count ? context.Runtime.Clone(cells[column]) : null);
            result.Add(line);
        }
        yield return result;
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
        foreach (JsonNode?[] combo in Combine(0))
        {
            var packed = new List<List<JsonNode?>>(combo.Length);
            foreach (JsonNode? item in combo)
                packed.Add(new List<JsonNode?> { item });
            foreach (JsonNode? value in context.Runtime.Range(packed))
                yield return value;
        }

        IEnumerable<JsonNode?[]> Combine(int index)
        {
            if (index >= args.Count)
            {
                yield return (JsonNode?[])current.Clone();
                yield break;
            }
            foreach (JsonNode? value in args[index].Evaluate(input, context, environment))
            {
                current[index] = value;
                foreach (JsonNode?[] combo in Combine(index + 1))
                    yield return combo;
            }
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
            foreach (JsonNode? combo in Combos(RequireMatrix(input), context))
                yield return combo;
            yield break;
        }
        foreach (JsonNode? count in Count.Evaluate(input, context, environment))
        {
            int times = (int)Number(count);
            var matrix = new List<JsonNode?>();
            for (int index = 0; index < times; index++)
            {
                context.Budget.CheckCancellation();
                context.Budget.ChargeNode();
                matrix.Add(input);
            }
            foreach (JsonNode? combo in Combos(matrix, context))
                yield return combo;
        }
    }

    private static List<JsonNode?> RequireMatrix(JsonNode? input)
    {
        if (input is JsonArray matrix)
        {
            var rows = new List<JsonNode?>(matrix.Count);
            foreach (JsonNode? row in matrix)
                rows.Add(row);
            return rows;
        }
        throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(input)}");
    }

    // Iterative odometer over row positions. Recursion depth here would be input
    // length, which the node budget does not bound below a CLR stack overflow, so
    // nested enumerator frames are not an option. Row 0 stays outermost like the
    // reference recursion. Rows validate lazily left-to-right stopping after the
    // first empty row: an empty leading row yields nothing without touching later
    // rows, matching short-circuiting in the reference definition.
    private static IEnumerable<JsonNode?> Combos(IReadOnlyList<JsonNode?> matrix, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(context);
        var rows = new List<JsonArray>();
        foreach (JsonNode? row in matrix)
        {
            if (row is not JsonArray cells)
                throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(row)}");
            rows.Add(cells);
            if (cells.Count == 0)
                yield break;
        }
        if (rows.Count == 0)
        {
            context.Budget.ChargeNode();
            yield return new JsonArray();
            yield break;
        }
        var positions = new int[rows.Count];
        while (true)
        {
            context.Budget.CheckCancellation();
            var combo = new JsonArray();
            context.Budget.ChargeNode();
            for (int index = 0; index < rows.Count; index++)
            {
                context.Budget.ChargeNode();
                combo.Add(context.Runtime.Clone(rows[index][positions[index]]));
            }
            yield return combo;
            int cursor = rows.Count - 1;
            while (cursor >= 0)
            {
                context.Budget.CheckCancellation();
                if (++positions[cursor] < rows[cursor].Count)
                    break;
                positions[cursor] = 0;
                cursor--;
            }
            if (cursor < 0)
                yield break;
        }
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
            int low = 0;
            int high = sorted.Count;
            int found = -1;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                int order = JqRuntime.Compare(target, sorted[middle]);
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

// Shared key precomputation for the `_by` family: one collected key-array
// per element, mirroring `map([f])`. Iteration follows `.[]`, so objects
// contribute values and null contributes nothing.
internal static class CollectionKeys
{
    internal static List<(JsonNode? Element, JsonNode Keys)> Keyed(JsonNode? input, JqFilter keys, JqContext context, JqEnvironment environment)
    {
        var keyed = new List<(JsonNode? Element, JsonNode Keys)>();
        foreach (JsonNode? element in MapFilter.Iterate(input, context, environment))
        {
            var collected = new JsonArray();
            foreach (JsonNode? key in keys.Evaluate(element, context, environment))
            {
                context.Budget.ChargeNode();
                collected.Add(context.Runtime.Clone(key));
            }
            keyed.Add((element, collected));
        }
        return keyed;
    }

    internal static IComparer<JsonNode?> TotalOrder() => Comparer<JsonNode?>.Create(static (left, right) => JqRuntime.Compare(left, right));
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
        context.Budget.ChargeNode();
        foreach (var (element, _) in CollectionKeys.Keyed(input, Keys, context, environment).OrderBy(pair => pair.Keys, CollectionKeys.TotalOrder()))
        {
            context.Budget.ChargeNode();
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
        var ordered = CollectionKeys.Keyed(input, Keys, context, environment).OrderBy(pair => pair.Keys, CollectionKeys.TotalOrder()).ToList();
        var result = new JsonArray();
        context.Budget.ChargeNode();
        JsonArray? group = null;
        JsonNode? current = null;
        foreach (var (element, keys) in ordered)
        {
            if (group is null || !context.Runtime.JsonEquals(current, keys))
            {
                group = new JsonArray();
                context.Budget.ChargeNode();
                result.Add(group);
                current = keys;
            }
            context.Budget.ChargeNode();
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
        var ordered = CollectionKeys.Keyed(input, Keys, context, environment).OrderBy(pair => pair.Keys, CollectionKeys.TotalOrder()).ToList();
        var result = new JsonArray();
        context.Budget.ChargeNode();
        JsonNode? current = null;
        bool first = true;
        foreach (var (element, keys) in ordered)
        {
            if (first || !context.Runtime.JsonEquals(current, keys))
            {
                context.Budget.ChargeNode();
                result.Add(context.Runtime.Clone(element));
                current = keys;
                first = false;
            }
        }
        yield return result;
    }
}

// `min_by(f)` and `max_by(f)`: first extreme wins; empty yields null.
internal sealed class MinMaxByFilter(JqFilter Keys, bool TakeMax) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JsonNode? best = null;
        JsonNode? bestKeys = null;
        bool empty = true;
        foreach (var (element, keys) in CollectionKeys.Keyed(input, Keys, context, environment))
        {
            int order = empty ? 0 : JqRuntime.Compare(keys, bestKeys);
            if (empty || (TakeMax ? order >= 0 : order < 0))
            {
                best = element;
                bestKeys = keys;
                empty = false;
            }
        }
        yield return empty ? null : context.Runtime.Clone(best);
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
            var pairs = new List<(JsonNode? Element, JsonNode? Key)>();
            for (int index = 0; index < elements.Count; index++)
            {
                context.Budget.ChargeNode();
                pairs.Add((elements[index], keys[index]));
            }
            var result = new JsonArray();
            context.Budget.ChargeNode();
            foreach (var pair in pairs.OrderBy(static item => item.Key, CollectionKeys.TotalOrder()))
            {
                context.Budget.ChargeNode();
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
            var pairs = new List<(JsonNode? Element, JsonNode? Key)>();
            for (int index = 0; index < elements.Count; index++)
            {
                context.Budget.ChargeNode();
                pairs.Add((elements[index], keys[index]));
            }
            var ordered = pairs.OrderBy(static item => item.Key, CollectionKeys.TotalOrder()).ToList();
            var result = new JsonArray();
            context.Budget.ChargeNode();
            JsonArray? group = null;
            JsonNode? current = null;
            foreach (var pair in ordered)
            {
                if (group is null || !context.Runtime.JsonEquals(current, pair.Key))
                {
                    group = new JsonArray();
                    context.Budget.ChargeNode();
                    result.Add(group);
                    current = pair.Key;
                }
                context.Budget.ChargeNode();
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
            var pairs = new List<(JsonNode? Element, JsonNode? Key)>();
            for (int index = 0; index < elements.Count; index++)
            {
                context.Budget.ChargeNode();
                pairs.Add((elements[index], keys[index]));
            }
            var ordered = pairs.OrderBy(static item => item.Key, CollectionKeys.TotalOrder()).ToList();
            var result = new JsonArray();
            context.Budget.ChargeNode();
            JsonNode? current = null;
            bool first = true;
            foreach (var pair in ordered)
            {
                if (first || !context.Runtime.JsonEquals(current, pair.Key))
                {
                    context.Budget.ChargeNode();
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
            JsonNode? best = elements[0];
            JsonNode? bestKey = keys[0];
            for (int index = 1; index < elements.Count; index++)
            {
                context.Budget.ChargeNode();
                int order = JqRuntime.Compare(keys[index], bestKey);
                if (TakeMax ? order >= 0 : order < 0)
                {
                    best = elements[index];
                    bestKey = keys[index];
                }
            }
            yield return context.Runtime.Clone(best);
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
            double level = Number(depth);
            yield return context.Runtime.Flatten(input, level);
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
        context.Budget.ChargeNode();
        foreach (JsonNode? row in rows)
        {
            foreach (JsonNode? key in IndexExpr.Evaluate(row, context, environment))
            {
                string name = context.Runtime.ToJqString(key);
                context.Budget.ChargeNode();
                context.Budget.ChargeString(name.Length);
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
                var collected = new JsonArray();
                context.Budget.ChargeNode();
                foreach (JsonNode? row in MapFilter.Iterate(input, context, environment))
                {
                    foreach (JsonNode? key in IndexExpr.Evaluate(row, context, environment))
                    {
                        JsonNode? lookup = LookupIndex(idx, key, context);
                        var pair = new JsonArray(context.Runtime.Clone(row), context.Runtime.Clone(lookup));
                        context.Budget.ChargeNode();
                        context.Budget.ChargeNode();
                        collected.Add(pair);
                    }
                }
                yield return collected;
            }
            else
            {
                foreach (JsonNode? row in Stream.Evaluate(input, context, environment))
                {
                    foreach (JsonNode? key in IndexExpr.Evaluate(row, context, environment))
                    {
                        JsonNode? lookup = LookupIndex(idx, key, context);
                        var pair = new JsonArray(context.Runtime.Clone(row), context.Runtime.Clone(lookup));
                        context.Budget.ChargeNode();
                        context.Budget.ChargeNode();
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
        }
    }

    private static JsonNode? LookupIndex(JsonNode? idx, JsonNode? key, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (idx is null)
            return null;
        if (idx is JsonObject obj && TryGetString(key, out string? name) && name is not null)
            return context.Runtime.Clone(obj.TryGetPropertyValue(name, out JsonNode? child) ? child : null);
        if (idx is JsonArray arr && JqPaths.TryGetIndex(key, out long index, out bool isNaN))
        {
            if (isNaN)
                return null;
            long resolved = index < 0 ? arr.Count + index : index;
            return resolved >= 0 && resolved < arr.Count ? context.Runtime.Clone(arr[(int)resolved]) : null;
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
        foreach (JsonNode? generated in Generator.Evaluate(input, context, environment))
            foreach (JsonNode? probe in Condition.Evaluate(generated, context, environment))
                if (Truthy(probe))
                {
                    yield return JsonValue.Create(true);
                    yield break;
                }
        yield return JsonValue.Create(false);
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
        foreach (JsonNode? generated in Generator.Evaluate(input, context, environment))
            foreach (JsonNode? probe in Condition.Evaluate(generated, context, environment))
                if (!Truthy(probe))
                {
                    yield return JsonValue.Create(false);
                    yield break;
                }
        yield return JsonValue.Create(true);
    }
}
