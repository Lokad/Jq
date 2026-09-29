using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

internal abstract class JqFilter
{
    public IEnumerable<JsonNode?> Evaluate(JsonNode? input, JqContext context)
    {
        context.Budget.EnterEvaluation();
        try
        {
            foreach (var value in EvaluateCore(input, context))
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
    protected abstract IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context);
}

internal sealed class IdentityFilter : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        yield return context.Runtime.Clone(input);
    }
}

internal sealed class LiteralFilter(JsonNode? value) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        yield return context.Runtime.Clone(value);
    }
}

internal sealed class VariableFilter(string name) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        if (!context.Variables.TryGetValue(name, out var value))
            throw new JqException($"undefined variable ${name}");
        yield return context.Runtime.Clone(value);
    }
}

internal sealed class PipeFilter(JqFilter left, JqFilter right) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        foreach (var value in left.Evaluate(input, context))
            foreach (var output in right.Evaluate(value, context))
                yield return output;
    }
}

internal sealed class CommaFilter(JqFilter left, JqFilter right) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        foreach (var value in left.Evaluate(input, context))
            yield return value;
        foreach (var value in right.Evaluate(input, context))
            yield return value;
    }
}

internal sealed class FieldFilter(JqFilter source, string name, bool optional) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        foreach (var value in source.Evaluate(input, context))
        {
            if (value is JsonObject obj)
                yield return context.Runtime.Clone(obj.TryGetPropertyValue(name, out var child) ? child : null);
            else if (optional || value == null)
                yield return null;
            else
                throw new JqRuntimeException($"cannot index {TypeName(value)} with string \"{name}\"");
        }
    }
}

internal sealed class IndexFilter(JqFilter source, JqFilter index, bool optional) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        // Key-major order: each key combines with every source value,
        // matching index-then-source evaluation with backtracking.
        foreach (var key in index.Evaluate(input, context))
            foreach (var value in source.Evaluate(input, context))
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
                else if (optional || value == null)
                {
                    yield return null;
                }
                else
                {
                    throw new JqRuntimeException($"cannot index {TypeName(value)}");
                }
            }
    }
}

internal sealed class SliceFilter(JqFilter source, JqFilter? start, JqFilter? end, bool optional) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        // Bound-major order: each start combines with every end, then every
        // source value, matching key-object construction with backtracking.
        foreach (var startIndex in StartIndexes())
            foreach (var endBound in EndBounds())
                foreach (var value in source.Evaluate(input, context))
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
                    else if (optional || value == null)
                    {
                        yield return null;
                    }
                    else
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
            foreach (var bound in start.Evaluate(input, context))
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
            foreach (var bound in end.Evaluate(input, context))
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        foreach (var value in source.Evaluate(input, context))
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

internal sealed class ArrayFilter(JqFilter item) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        var array = new JsonArray();
        foreach (var value in item.Evaluate(input, context))
            array.Add(context.Runtime.Clone(value));
        yield return array;
    }
}

internal sealed class ObjectFilter(IReadOnlyList<(string Key, JqFilter Value)> properties) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
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

            var (key, filter) = properties[index];
            // Lazy cartesian: an empty value stream yields no objects.
            foreach (var value in filter.Evaluate(input, context))
            {
                if (context.Runtime.Clone(current) is not JsonObject next)
                    throw new InvalidOperationException("Expected object clone.");
                next[key] = context.Runtime.Clone(value);
                foreach (var obj in Build(index + 1, next))
                    yield return obj;
            }
        }
    }
}

internal sealed class BinaryFilter(JqFilter left, string op, JqFilter right) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        foreach (var l in left.Evaluate(input, context))
            foreach (var r in right.Evaluate(input, context))
                yield return Eval(l, r);

        JsonNode? Eval(JsonNode? l, JsonNode? r)
        {
            return op switch
            {
                "+" => context.Runtime.Add(l, r),
                "-" => JsonValue.Create(Number(l) - Number(r)),
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
                "//" => Truthy(l) ? context.Runtime.Clone(l) : context.Runtime.Clone(r),
                _ => throw new JqException($"unsupported operator {op}")
            };

            JsonNode? Divide(JsonNode? l, JsonNode? r)
            {
                double left = Number(l);
                double right = Number(r);
                if (right == 0.0 && TypeName(l) == "number" && TypeName(r) == "number")
                    throw new JqRuntimeException($"number ({context.Runtime.ToJqString(l)}) and number ({context.Runtime.ToJqString(r)}) cannot be divided because the divisor is zero");
                return JsonValue.Create(left / right);
            }

            // Integer remainder matching the reference: operands truncate
            // toward zero with clamping, NaN propagates, zero divisors fail.
            JsonNode? Modulo(JsonNode? l, JsonNode? r)
            {
                double left = Number(l);
                double right = Number(r);
                if (TypeName(l) == "number" && TypeName(r) == "number")
                {
                    if (double.IsNaN(left) || double.IsNaN(right))
                        return JsonValue.Create(double.NaN);
                    long divisor = Truncate(right);
                    if (divisor == 0)
                        throw new JqRuntimeException($"number ({context.Runtime.ToJqString(l)}) and number ({context.Runtime.ToJqString(r)}) cannot be divided (remainder) because the divisor is zero");
                    if (divisor == -1)
                        return JsonValue.Create(0L);
                    return JsonValue.Create(Truncate(left) % divisor);
                }
                return JsonValue.Create(left % right);
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

internal sealed class UnaryFilter(string op, JqFilter inner) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        foreach (var value in inner.Evaluate(input, context))
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        foreach (var (condition, then) in branches)
        {
            if (condition.Evaluate(input, context).Any(Truthy))
            {
                foreach (var value in then.Evaluate(input, context))
                    yield return value;
                yield break;
            }
        }

        foreach (var value in otherwise.Evaluate(input, context))
            yield return value;
    }
}

internal sealed class FunctionFilter(string name, IReadOnlyList<JqFilter> args) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        if (name == "empty")
            yield break;

        if (name == "select")
        {
            if (args.Count != 1)
                throw new JqException("select expects one argument");
            // Predicates can yield multiple results; consume them without buffering.
            foreach (var value in args[0].Evaluate(input, context))
                if (Truthy(value))
                    yield return context.Runtime.Clone(input);
            yield break;
        }

        var evaluated = args.Select(a => a.Evaluate(input, context).ToList()).ToList();
        JsonNode? Arg(int i) => evaluated.Count > i && evaluated[i].Count > 0 ? evaluated[i][0] : null;

        switch (name)
        {
            case "length": yield return JsonValue.Create(Length(input)); break;
            case "type": yield return JsonValue.Create(TypeName(input)); break;
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
                foreach (var value in context.Runtime.Range(evaluated))
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

internal sealed class FormatFilter(string format) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        yield return JsonValue.Create(context.Runtime.Format(format, input));
    }

}

internal sealed class InterpolatedStringFilter(string template, string? format) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '\\' && i + 1 < template.Length && template[i + 1] == '(')
            {
                var start = i + 2;
                var depth = 1;
                i = start;
                for (; i < template.Length; i++)
                {
                    if (template[i] == '(') depth++;
                    else if (template[i] == ')' && --depth == 0) break;
                }
                if (depth != 0)
                    throw new JqException("unterminated string interpolation");
                var expr = template[start..i];
                var values = new JqParser(expr, context.ProgramSource, context.Variables, context.Budget).Parse().Evaluate(input, context);
                var found = false;
                JsonNode? last = null;
                foreach (var value in values)
                {
                    found = true;
                    last = value;
                }
                if (found)
                    context.Budget.Append(sb, format is null
                        ? context.Runtime.ToJqString(last)
                        : context.Runtime.Format(format, last));
            }
            else
            {
                context.Budget.Append(sb, template.AsSpan(i, 1));
            }
        }

        yield return JsonValue.Create(context.Budget.Finish(sb));
    }
}
