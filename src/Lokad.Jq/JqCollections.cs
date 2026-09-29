using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// `map(f)`: collects every f-output per element into a new array.
internal sealed class MapFilter(JqFilter Body) : JqFilter
{
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (input is not JsonArray rows)
            throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(input)}");
        var matrix = new List<JsonNode?>();
        int width = 0;
        foreach (JsonNode? row in rows)
        {
            if (row is not JsonArray && row is not null)
                throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(row)}");
            int length = row is JsonArray present ? present.Count : 0;
            if (length > width)
                width = length;
            context.Budget.ChargeNode();
            matrix.Add(row);
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

// `combinations` and `combinations(n)`: cartesian products of array rows.
internal sealed class CombinationsFilter(JqFilter? Count) : JqFilter
{
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
                matrix.Add(input);
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

    private static IEnumerable<JsonNode?> Combos(IReadOnlyList<JsonNode?> matrix, JqContext context)
    {
        if (matrix.Count == 0)
        {
            yield return new JsonArray();
            yield break;
        }
        if (matrix[0] is not JsonArray first)
            throw new JqRuntimeException($"cannot iterate over {JqRuntime.TypeName(matrix[0])}");
        var rest = new List<JsonNode?>();
        for (int index = 1; index < matrix.Count; index++)
            rest.Add(matrix[index]);
        foreach (JsonNode? head in first)
            foreach (JsonNode? tail in Combos(rest, context))
            {
                var combo = new JsonArray();
                context.Budget.ChargeNode();
                combo.Add(head?.DeepClone());
                if (tail is JsonArray suffix)
                    foreach (JsonNode? item in suffix)
                        combo.Add(item?.DeepClone());
                yield return combo;
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
