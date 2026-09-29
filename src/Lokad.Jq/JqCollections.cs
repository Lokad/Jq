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
