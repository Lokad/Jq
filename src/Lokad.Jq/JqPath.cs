using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// Path segments for `path`, assignment, and update machinery. Indices stay
// raw (negative values resolve against the live container at use time);
// slices keep optional raw bounds with the same resolution.
internal abstract record JqValueSegment
{
    // Raw values are borrowed for diagnostics; PathToJson copies before parenting.
    internal abstract JsonNode? ToJson();
}

internal sealed record KeySegment(string Key) : JqValueSegment
{
    internal override JsonNode? ToJson() => JsonValue.Create(Key);
}

internal sealed record IndexSegment(long Index, bool IsNaN) : JqValueSegment
{
    internal override JsonNode? ToJson() => IsNaN ? JsonValue.Create(double.NaN) : JsonValue.Create(Index);
}

// A fractional array index preserves its double for rendering and deletion
// bucketing; reads and writes truncate it toward zero like the reference.
internal sealed record FractionalSegment(double Value) : JqValueSegment
{
    internal override JsonNode? ToJson() => JsonValue.Create(Value);
}

internal sealed record SliceSegment(double? Start, double? End) : JqValueSegment
{
    internal override JsonNode? ToJson()
    {
        var slice = new JsonObject();
        slice["start"] = Start.HasValue ? JqRuntime.CreateNumber(Start.Value) : null;
        slice["end"] = End.HasValue ? JqRuntime.CreateNumber(End.Value) : null;
        return slice;
    }
}

// A slice object carried verbatim from a path value or an index-form key
// (getpath/setpath/delpaths arguments, .[{...}] reads). Unlike evaluated
// syntax slices, its keys may be missing or non-numeric: the reference
// parse_slice step rejects those only against arrays and strings, while
// other containers report their own kind errors, so validation stays at
// the use sites and rendering keeps the raw key set.
internal sealed record SliceObjectSegment(JsonObject Raw) : JqValueSegment
{
    internal override JsonNode? ToJson() => Raw;
}

// A segment that can never address a container (booleans, arrays, and nulls
// from path values). Traversal reports container-shaped type errors.
internal sealed record InvalidSegment(JsonNode? Raw) : JqValueSegment
{
    internal override JsonNode? ToJson() => Raw;
}

// One enumerated path: accumulated segments, the value found there, and
// whether the value survived intact (fresh values travel untracked and fail
// at the next path boundary).
internal sealed record JqValuePath(IReadOnlyList<JqValueSegment> Segments, JsonNode? Value, bool Tracked)
{
    // Keep the selected value so a later identical result can restore its path.
    internal JqValuePath? Origin { get; init; }

    internal JqValuePath WithValue(JsonNode? value, bool tracked) =>
        new(Segments, value, tracked) { Origin = tracked ? null : Tracked ? this : Origin };
}

internal static class JqPaths
{
    internal const int MaxPathDepth = 10000;

    internal static string InvalidResult(JsonNode? value, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return "Invalid path expression with result " + context.Runtime.TruncatedDump(value);
    }

    internal static string InvalidAccess(JsonNode? segment, JsonNode? container, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return "Invalid path expression near attempt to access element "
            + context.Runtime.TruncatedDump(segment)
            + " of "
            + context.Runtime.TruncatedDump(container);
    }

    internal static string InvalidIterate(JsonNode? value, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return "Invalid path expression near attempt to iterate through " + context.Runtime.TruncatedDump(value);
    }

    // Collects tracked segments; fresh values fail exactly like upstream.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<List<IReadOnlyList<JqValueSegment>>> CollectPaths(JqFilter paths, JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var collected = new List<IReadOnlyList<JqValueSegment>>();
        await foreach (JqValuePath pair in paths.EvaluatePaths(new JqValuePath(new List<JqValueSegment>(), input, true), context, environment).ConfigureAwait(false))
        {
            if (!pair.Tracked)
                throw new JqException(InvalidResult(pair.Value, context));
            context.Budget.ReserveBytes(24);
            collected.Add(pair.Segments);
        }
        return collected;
    }

    internal static IReadOnlyList<JqValueSegment> Extend(IReadOnlyList<JqValueSegment> segments,
        JqValueSegment segment, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(segment);
        budget.ReserveBytes(128 + 16L * (segments.Count + 1));
        var extended = new List<JqValueSegment>(segments.Count + 1);
        foreach (JqValueSegment existing in segments)
            extended.Add(existing);
        extended.Add(segment);
        return extended;
    }

    // Path boundaries require intact tracking; fresh values fail with the
    // upstream access-shape message.
    internal static void RequireTracked(JqValuePath pair, JqValueSegment segment, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(context);
        if (!pair.Tracked)
            throw new JqException(InvalidAccess(segment.ToJson(), pair.Value, context));
    }

    internal static JsonNode PathToJson(IReadOnlyList<JqValueSegment> segments, JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(segments);
        budget.ReserveBytes(64 + 88L * segments.Count);
        var path = new JsonArray();
        foreach (JqValueSegment segment in segments)
        {
            if (segment is SliceObjectSegment slice)
                path.Add(budget.CopyValue(slice.Raw, 1));
            else if (segment is InvalidSegment invalid)
                path.Add(budget.CopyValue(invalid.Raw, 1));
            else
            {
                if (segment is SliceSegment) budget.ReserveBytes(384);
                path.Add(segment.ToJson());
            }
        }
        return path;
    }

    // Parses a path value (getpath/setpath/delpaths arguments). Numbers
    // truncate toward zero; NaN indices are kept for downstream handling.
    internal static List<JqValueSegment> ParsePathValue(JsonNode? path, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (path is not JsonArray array)
            throw new JqException("Path must be specified as an array");
        if (array.Count > MaxPathDepth)
            throw new JqException("Path too deep");
        context.Budget.ReserveBytes(64L * array.Count);
        var segments = new List<JqValueSegment>();
        foreach (JsonNode? element in array)
        {
            context.Budget.CheckCancellation();
            if (TryGetString(element, out string? key))
                segments.Add(new KeySegment(key));
            else if (TryGetFractional(element, out double fractional))
                segments.Add(new FractionalSegment(fractional));
            else if (TryGetIndex(element, out long index, out bool isNaN))
                segments.Add(new IndexSegment(index, isNaN));
            else if (element is JsonObject slice)
            {
                segments.Add(new SliceObjectSegment(slice));
            }
            else
            {
                segments.Add(new InvalidSegment(element));
            }
        }
        return segments;
    }

    // Integral storage does not convert through TryGetValue<double>, so
    // probe whole numbers first; fractional values truncate toward zero.
    internal static bool TryGetIndex(JsonNode? key, out long index, out bool isNaN)
    {
        index = 0;
        isNaN = false;
        if (key is JsonValue small && small.TryGetValue<int>(out int directInt))
        {
            index = directInt;
            return true;
        }
        if (key is JsonValue whole && whole.TryGetValue<long>(out long direct))
        {
            index = direct;
            return true;
        }
        if (key is JsonValue real && real.TryGetValue<double>(out double value))
        {
            if (double.IsNaN(value))
            {
                isNaN = true;
                return true;
            }
            index = (long)ClampDouble(value);
            return true;
        }
        return false;
    }

    // Reads and writes truncate fractional indices toward zero like the
    // reference casts; deletions resolve the sign separately.
    internal static long TruncateIndex(double value) => (long)ClampDouble(value);

    internal static bool TryGetFractional(JsonNode? key, out double value)
    {
        value = 0;
        if (key is JsonValue real && real.TryGetValue<double>(out double candidate) &&
            double.IsFinite(candidate) && candidate != Math.Truncate(candidate))
        {
            value = candidate;
            return true;
        }
        return false;
    }

    private static double ClampDouble(double value)
    {
        if (value < long.MinValue) return long.MinValue;
        if (value > long.MaxValue) return long.MaxValue;
        return value;
    }

    // Path enumeration preserves exotic keys verbatim for rendering, while
    // the reference re-parses path values at every update boundary with
    // objects reading as slices; carry those segments raw so the use sites
    // can apply the reference container-dependent dispatch.
    internal static JqValueSegment UpdateSegment(JqValueSegment segment)
    {
        if (segment is InvalidSegment invalid && invalid.Raw is JsonObject slice)
            return new SliceObjectSegment(slice);
        return segment;
    }

    // Reads and writes share one truncation for fractional segments; the
    // deletion grouping below resolves the sign itself.
    internal static JqValueSegment TruncateFractional(JqValueSegment segment)
    {
        if (segment is FractionalSegment fractional)
            return new IndexSegment(TruncateIndex(fractional.Value), false);
        return segment;
    }

    // Reads slice-object bounds like the reference parse_slice key lookup:
    // both keys must be present (explicit nulls and NaN take the same
    // container-dependent defaults as evaluated bounds); numbers keep their
    // values and anything else fails the slice at the use site.
    internal static bool TryGetSliceObjectBounds(JsonObject slice, out double? start, out double? end)
    {
        start = null;
        end = null;
        if (!slice.TryGetPropertyValue("start", out JsonNode? startNode) ||
            !slice.TryGetPropertyValue("end", out JsonNode? endNode))
            return false;
        if (!TrySliceBoundValue(startNode, out start) || !TrySliceBoundValue(endNode, out end))
            return false;
        return true;
    }

    internal static bool TrySliceBoundValue(JsonNode? bound, out double? value)
    {
        if (bound is null)
        {
            value = null;
            return true;
        }
        if (bound is JsonValue small && small.TryGetValue<int>(out int directInt))
        {
            value = directInt;
            return true;
        }
        if (bound is JsonValue whole && whole.TryGetValue<long>(out long direct))
        {
            value = direct;
            return true;
        }
        if (bound is JsonValue real && real.TryGetValue<double>(out double candidate))
        {
            value = double.IsNaN(candidate) ? null : candidate;
            return true;
        }
        value = null;
        return false;
    }

    // Resolves raw slice bounds against a live length with the reference
    // rules: missing bounds default, NaN behaves like missing, negatives
    // offset from the length, the start truncates toward zero while a
    // fractional end within bounds rounds up, and an empty remainder
    // collapses to the start.
    internal static void ResolveSlice(int count, double? start, double? end, out int from, out int to)
    {
        double lower = start ?? 0;
        double upper = end ?? count;
        if (double.IsNaN(lower))
            lower = 0;
        if (double.IsNaN(upper))
            upper = count;
        if (lower < 0)
            lower += count;
        if (lower < 0)
            lower = 0;
        if (lower > count)
            lower = count;
        int resolvedStart = lower > int.MaxValue ? int.MaxValue : (int)lower;
        if (upper < 0)
            upper += count;
        if (upper < 0)
            upper = resolvedStart;
        int resolvedEnd = upper > int.MaxValue ? int.MaxValue : (int)upper;
        if (resolvedEnd > count)
            resolvedEnd = count;
        if (resolvedEnd < count)
            resolvedEnd += resolvedEnd < upper ? 1 : 0;
        if (resolvedEnd < resolvedStart)
            resolvedEnd = resolvedStart;
        from = resolvedStart;
        to = resolvedEnd;
    }
}

// Path updates deep-copy containers on the spine before mutation. JsonNode
// children have one parent; source values and sibling outputs remain independent.
internal static class JqPathUpdates
{
    private sealed record Frame(JqValueSegment Segment, JsonNode Container, int Index, int From, int To);

    internal static JsonNode? SetPath(JsonNode? root, IReadOnlyList<JqValueSegment> segments, JsonNode? value, JqContext context) =>
        UpdatePath(root, segments, value, context, false);

    // The caller owns the complete tree and has not exposed it to a consumer.
    internal static JsonNode? SetOwnedPath(JsonNode? root, IReadOnlyList<JqValueSegment> segments, JsonNode? value, JqContext context) =>
        UpdatePath(root, segments, value, context, true);

    private static JsonNode? UpdatePath(JsonNode? root, IReadOnlyList<JqValueSegment> segments, JsonNode? value, JqContext context, bool owned)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(context);
        if (segments.Count > JqPaths.MaxPathDepth)
            throw new JqException("Path too deep");
        if (segments.Count == 0)
            return context.Runtime.Clone(value);
        // Frame objects and geometrically replaced backing slots.
        context.Budget.ReserveBytes(80L * segments.Count);
        var frames = new Stack<Frame>();
        JsonNode? current = root;
        for (int depth = 0; depth < segments.Count; depth++)
            current = Descend(current, JqPaths.TruncateFractional(JqPaths.UpdateSegment(segments[depth])), frames, context, owned);
        JsonNode? leaf = context.Runtime.Clone(value);
        while (frames.TryPop(out Frame? frame))
            leaf = ApplyFrame(frame, leaf, context);
        return leaf;
    }

    private static JsonNode? Descend(JsonNode? current, JqValueSegment segment, Stack<Frame> frames, JqContext context, bool owned)
    {
        switch (segment)
        {
            case KeySegment key:
                if (current is null)
                {
                    frames.Push(new Frame(segment, NewObject(context), 0, 0, 0));
                    return null;
                }
                if (current is JsonObject obj)
                {
                    frames.Push(new Frame(segment, owned ? obj : (JsonObject)context.Budget.CopyValue(obj, 0), 0, 0, 0));
                    return obj.TryGetPropertyValue(key.Key, out JsonNode? child) ? child : null;
                }
                // Like the reference probe read inside jv_setpath, indexing a
                // non-object with a key reports the container and key kinds.
                throw new JqException("Cannot index " + JqRuntime.TypeName(current) + " with string (" + context.Runtime.TruncatedDump(JsonValue.Create(key.Key)) + ")");
            case IndexSegment index:
                if (index.IsNaN)
                    throw new JqException("Cannot set array element at NaN index");
                if (current is null)
                {
                    if (index.Index < 0)
                        throw new JqException("Out of bounds negative array index");
                    if (index.Index >= Array.MaxLength)
                        throw new JqException("Array index too large");
                    frames.Push(new Frame(segment, PaddedArray(null, index.Index, context, owned), (int)index.Index, 0, 0));
                    return null;
                }
                if (current is JsonArray arr)
                {
                    long resolved = index.Index < 0 ? arr.Count + index.Index : index.Index;
                    if (resolved < 0)
                        throw new JqException("Out of bounds negative array index");
                    if (resolved >= Array.MaxLength)
                        throw new JqException("Array index too large");
                    frames.Push(new Frame(segment, PaddedArray(arr, resolved, context, owned), (int)resolved, 0, 0));
                    return resolved < arr.Count ? arr[(int)resolved] : null;
                }
                // Like the reference probe read inside jv_setpath, indexing a
                // non-array reports the container and key kinds.
                throw new JqException("Cannot index " + JqRuntime.TypeName(current) + " with number (" + index.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            case SliceSegment slice:
                if (current is null)
                {
                    var fresh = new JsonArray();
                    context.Budget.CheckCancellation();
                    frames.Push(new Frame(segment, fresh, 0, 0, 0));
                    return SliceCopy(fresh, 0, 0, context);
                }
                if (current is JsonArray target)
                {
                    JqPaths.ResolveSlice(target.Count, slice.Start, slice.End, out int from, out int to);
                    frames.Push(new Frame(segment, owned ? target : (JsonArray)context.Budget.CopyValue(target, 0), 0, from, to));
                    return SliceCopy(target, from, to, context);
                }
                if (current is JsonValue scalar && scalar.TryGetValue<string>(out _))
                    throw new JqException("Cannot update string slices");
                // Like the reference setpath descent, a slice update on any
                // other scalar fails first in the nested probe read with the
                // container and key kinds (objects included: the jv_set
                // fallthrough below is unreachable through jq programs).
                throw new JqRuntimeException("Cannot index " + JqRuntime.TypeName(current) + " with object (" + context.Runtime.TruncatedDump(slice.ToJson()) + ")");
            case SliceObjectSegment raw:
                if (current is null)
                {
                    if (!JqPaths.TryGetSliceObjectBounds(raw.Raw, out double? emptyStart, out double? emptyEnd))
                        throw new JqException("Array/string slice indices must be integers");
                    var freshRaw = new JsonArray();
                    context.Budget.CheckCancellation();
                    frames.Push(new Frame(new SliceSegment(emptyStart, emptyEnd), freshRaw, 0, 0, 0));
                    return SliceCopy(freshRaw, 0, 0, context);
                }
                if (current is JsonArray rawTarget)
                {
                    if (!JqPaths.TryGetSliceObjectBounds(raw.Raw, out double? rawStart, out double? rawEnd))
                        throw new JqException("Array/string slice indices must be integers");
                    JqPaths.ResolveSlice(rawTarget.Count, rawStart, rawEnd, out int rawFrom, out int rawTo);
                    frames.Push(new Frame(new SliceSegment(rawStart, rawEnd), owned ? rawTarget : (JsonArray)context.Budget.CopyValue(rawTarget, 0), 0, rawFrom, rawTo));
                    return SliceCopy(rawTarget, rawFrom, rawTo, context);
                }
                if (current is JsonValue rawScalar && rawScalar.TryGetValue<string>(out _))
                {
                    // The nested probe read runs before the string-slice
                    // rejection, so partial objects fail on their bounds.
                    if (!JqPaths.TryGetSliceObjectBounds(raw.Raw, out double? _, out double? _))
                        throw new JqException("Array/string slice indices must be integers");
                    throw new JqException("Cannot update string slices");
                }
                throw new JqRuntimeException("Cannot index " + JqRuntime.TypeName(current) + " with object (" + context.Runtime.TruncatedDump(raw.Raw) + ")");
            default:
                throw SegmentTypeError(current, segment, context);
        }
    }

    private static JsonNode ApplyFrame(Frame frame, JsonNode? leaf, JqContext context)
    {
        switch (frame.Segment)
        {
            case KeySegment key when frame.Container is JsonObject obj:
                if (!obj.ContainsKey(key.Key)) context.Budget.ReserveObjectItem(leaf);
                else if (ReferenceEquals(obj[key.Key], leaf)) return obj;
                obj[key.Key] = context.Runtime.Detach(leaf);
                return obj;
            case IndexSegment when frame.Container is JsonArray arr:
                if (ReferenceEquals(arr[frame.Index], leaf)) return arr;
                arr[frame.Index] = context.Runtime.Detach(leaf);
                return arr;
            case SliceSegment when frame.Container is JsonArray arr:
                if (leaf is not JsonArray pieces)
                    throw new JqException("A slice of an array can only be assigned another array");
                var rebuilt = new JsonArray();
                context.Budget.CheckCancellation();
                for (int index = 0; index < frame.From; index++)
                {
                    context.Budget.ReserveArrayItem(arr[index]);
                    rebuilt.Add(context.Budget.CopyValue(arr[index], 1));
                }
                foreach (JsonNode? piece in pieces)
                {
                    context.Budget.ReserveArrayItem(piece);
                    rebuilt.Add(context.Budget.CopyValue(piece, 1));
                }
                for (int index = frame.To; index < arr.Count; index++)
                {
                    context.Budget.ReserveArrayItem(arr[index]);
                    rebuilt.Add(context.Budget.CopyValue(arr[index], 1));
                }
                return rebuilt;
            default:
                throw new InvalidOperationException("Mismatched path frame.");
        }
    }
    private static JsonObject NewObject(JqContext context)
    {
        context.Budget.CheckCancellation();
        return new JsonObject();
    }

    // Pads through the resolved index so frame application always lands
    // in range; every pad element is budget-charged.
    private static JsonArray PaddedArray(JsonArray? source, long resolved, JqContext context, bool owned)
    {
        long padding = Math.Max(0, resolved + 1 - (source?.Count ?? 0));
        // Admit all additional backing slots before copying or adding any padding.
        // An in-range update still admits its copy, with no padding charge.
        context.Budget.ReserveBytes(checked(24L * padding));
        JsonArray padded;
        if (source is not null)
            padded = owned ? source : (JsonArray)context.Budget.CopyValue(source, 0);
        else
        {
            context.Budget.ReserveBytes(64);
            padded = new JsonArray();
        }
        while (padded.Count <= resolved)
        {
            context.Budget.CheckCancellation();
            padded.Add(null);
        }
        return padded;
    }

    private static JsonArray SliceCopy(JsonArray source, int from, int to, JqContext context)
    {
        context.Budget.ReserveBytes(64 + 24L * (to - from));
        var slice = new JsonArray();
        for (int index = from; index < to; index++)
        {
            if (source[index] is JsonValue) context.Budget.ReserveBytes(64);
            slice.Add(context.Budget.CopyValue(source[index], 1));
        }
        return slice;
    }

    private static JqException SegmentTypeError(JsonNode? current, JqValueSegment segment, JqContext context)
    {
        string raw = context.Runtime.TruncatedDump(segment.ToJson());
        if (current is JsonArray)
            return new JqException("expected a number for indexing an array but got: " + raw);
        return new JqException("expected a string for object key but got: " + raw);
    }
}

internal static class JqPathReads
{
    internal static JsonNode? ReadIndex(JsonNode? value, long index, bool isNaN, JqContext context)
    {
        if (value is null) return null;
        if (value is not JsonArray array)
            throw new JqRuntimeException("Cannot index " + TypeName(value) + " with number ("
                + (isNaN ? "null" : index.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")");
        if (isNaN) return null;
        long resolved = index < 0 ? array.Count + index : index;
        return resolved >= 0 && resolved < array.Count ? array[(int)resolved] : null;
    }

    internal static bool TryIndex(JsonNode? value, JsonNode? key, bool optional, JqContext context, out JsonNode? result)
    {
        context.Budget.CheckCancellation();
        result = null;
        if (value is JsonArray array && TryGetArrayIndex(key, out long index))
        {
            long resolved = index < 0 ? array.Count + index : index;
            result = resolved >= 0 && resolved < array.Count ? array[(int)resolved] : null;
        }
        else if (value is JsonArray && key is JsonValue nan && nan.TryGetValue<double>(out double number) && double.IsNaN(number)) return true;
        else if (value is JsonArray && key is JsonArray pattern)
            result = context.Runtime.Indices(value, pattern);
        else if (key is JsonObject slice && (value is JsonArray || TryGetString(value, out _)))
        {
            if (!JqPaths.TryGetSliceObjectBounds(slice, out double? lower, out double? upper))
            {
                if (optional) return false;
                throw new JqException("Array/string slice indices must be integers");
            }
            result = Slice(value, lower, upper, context);
        }
        else if (value is JsonObject obj && TryGetString(key, out string name))
            result = obj.TryGetPropertyValue(name, out JsonNode? child) ? child : null;
        else if (value is null && key is not null && key is not JsonArray
            && !(key is JsonValue scalar && scalar.TryGetValue<bool>(out _))) return true;
        else
        {
            if (optional) return false;
            if (key is JsonObject objectKey && value is not null)
                throw new JqRuntimeException("Cannot index " + TypeName(value) + " with object (" + context.Runtime.TruncatedDump(objectKey) + ")");
            throw new JqRuntimeException("cannot index " + TypeName(value));
        }
        return true;
    }

    internal static bool TrySlice(JsonNode? value, JsonNode? lower, JsonNode? upper, bool optional,
        JqContext context, out JsonNode? result)
    {
        context.Budget.CheckCancellation();
        result = null;
        if (value is null) return true;
        if (value is JsonArray || TryGetString(value, out _))
        {
            if (!JqPaths.TrySliceBoundValue(lower, out double? start) || !JqPaths.TrySliceBoundValue(upper, out double? end))
            {
                if (optional) return false;
                throw new JqException("Array/string slice indices must be integers");
            }
            result = Slice(value, start, end, context);
            return true;
        }
        if (optional) return false;
        throw new JqRuntimeException("Cannot index " + TypeName(value) + " with object ("
            + context.Runtime.TruncatedDump(SliceKey(lower, upper, context)) + ")");
    }

    internal static bool TrySlicePath(JqValuePath pair, JsonNode? lower, JsonNode? upper, bool optional,
        JqContext context, [NotNullWhen(true)] out JqValuePath? result)
    {
        JqValueSegment Segment() => JqPaths.TrySliceBoundValue(lower, out double? from)
            && JqPaths.TrySliceBoundValue(upper, out double? to)
            ? new SliceSegment(from, to) : new InvalidSegment(SliceKey(lower, upper, context));
        if (!pair.Tracked) JqPaths.RequireTracked(pair, Segment(), context);
        result = null;
        if (!TrySlice(pair.Value, lower, upper, optional, context, out JsonNode? value)) return false;
        result = new JqValuePath(JqPaths.Extend(pair.Segments, Segment(), context.Budget), value, true);
        return true;
    }

    internal static JsonObject SliceKey(JsonNode? lower, JsonNode? upper, JqContext context)
    {
        context.Budget.ReserveBytes(384);
        return new JsonObject
        {
            ["start"] = context.Budget.CopyValue(lower, 1),
            ["end"] = context.Budget.CopyValue(upper, 1),
        };
    }

    private static JsonNode? Slice(JsonNode? value, double? lower, double? upper, JqContext context)
    {
        if (value is JsonArray array)
        {
            JqPaths.ResolveSlice(array.Count, lower, upper, out int from, out int to);
            context.Budget.ReserveBytes(64 + 24L * (to - from));
            var result = new JsonArray();
            for (int index = from; index < to; index++)
            {
                context.Budget.ReserveArrayItem(array[index]);
                result.Add(context.Budget.CopyValue(array[index], 1));
            }
            return result;
        }
        if (TryGetString(value, out string text)) return JsonValue.Create(context.Runtime.SliceString(text, lower, upper));
        if (value is null) return null;
        throw new JqRuntimeException("Cannot index " + TypeName(value) + " with object ("
            + context.Runtime.TruncatedDump(SliceKey(lower.HasValue ? CreateNumber(lower.Value) : null,
                upper.HasValue ? CreateNumber(upper.Value) : null, context)) + ")");
    }

    internal static JsonNode? ReadField(JsonNode? value, string key, JqRuntime runtime) => value switch
    {
        JsonObject obj => obj.TryGetPropertyValue(key, out JsonNode? child) ? child : null,
        null => null,
        _ => throw new JqRuntimeException("Cannot index " + JqRuntime.TypeName(value) + " with string ("
            + runtime.TruncatedDump(JsonValue.Create(key)) + ")")
    };

    // Read traversal mirroring value semantics: missing reads null,
    // mistyped containers raise the same errors as ordinary reads.
    internal static JsonNode? GetPath(JsonNode? root, IReadOnlyList<JqValueSegment> segments, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(context);
        context.Budget.CheckCancellation();
        JsonNode? current = root;
        foreach (JqValueSegment existing in segments)
        {
            context.Budget.CheckCancellation();
            JqValueSegment segment = JqPaths.TruncateFractional(JqPaths.UpdateSegment(existing));
            switch (segment)
            {
                case KeySegment key:
                    current = ReadField(current, key.Key, context.Runtime);
                    break;
                case IndexSegment index:
                    current = ReadIndex(current, index.Index, index.IsNaN, context);
                    break;
                case SliceSegment slice:
                    current = Slice(current, slice.Start, slice.End, context);
                    break;
                case SliceObjectSegment raw:
                    _ = TryIndex(current, raw.Raw, false, context, out current);
                    break;
                default:
                    if (segment is InvalidSegment invalid && invalid.Raw is JsonArray pattern && current is JsonArray)
                    {
                        // Like the reference get, an array key on an array
                        // searches contiguous subsequence positions.
                        current = context.Runtime.Indices(current, pattern);
                        break;
                    }
                    throw MismatchError(current, segment, context);
            }
        }
        return current;
    }

    private static JqException MismatchError(JsonNode? current, JqValueSegment segment, JqContext context)
    {
        string raw = context.Runtime.TruncatedDump(segment.ToJson());
        if (current is JsonArray)
            return new JqException("expected a number for indexing an array but got: " + raw);
        return new JqException("expected a string for object key but got: " + raw);
    }
}
internal static class JqPathDeletes
{
    internal static JsonNode? DeletePaths(JsonNode? root, IReadOnlyList<IReadOnlyList<JqValueSegment>> paths, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(context);
        context.Budget.CheckCancellation();
        if (paths.Count == 0) return root;
        foreach (var path in paths)
        {
            context.Budget.CheckCancellation();
            if (path.Count == 0) return null;
        }
        using var storage = new JqStorage(context.Budget.MemoryBudget);
        // Sorting workspace and the retained index vector. Paths stay borrowed.
        storage.ReserveBytes(64 + 32L * paths.Count);
        context.Budget.CheckCancellation();
        using var order = new PathOrder(context, storage);
        int[] sorted = JqComparer.OrderBy(Enumerable.Range(0, paths.Count), index => paths[index], order).ToArray();
        var frames = new List<DeleteFrame>();
        int active = 0;
        Push(root, 0, sorted.Length, 0, null);
        while (active != 0)
        {
            context.Budget.CheckCancellation();
            DeleteFrame frame = frames[active - 1];
            if (frame.Next < frame.Last)
            {
                int first = frame.Next;
                int last = first + 1;
                JqValueSegment key = JqPaths.UpdateSegment(paths[sorted[first]][frame.Depth]);
                while (last < frame.Last && order.Equal(key, JqPaths.UpdateSegment(paths[sorted[last]][frame.Depth]))) last++;
                frame.Next = last;
                if (paths[sorted[first]].Count == frame.Depth + 1)
                {
                    frame.Remove(key);
                    continue;
                }
                if (frame.IsArray && TrySlice(key, out double? start, out double? end))
                {
                    JqPaths.ResolveSlice(frame.Count, start, end, out int from, out int to);
                    Push(frame.Node, first, last, frame.Depth + 1, key).BorrowSlice(frame, from, to);
                }
                else
                {
                    JsonNode? child = frame.Read(key);
                    if (child is not null) Push(child, first, last, frame.Depth + 1, key);
                }
                continue;
            }
            JsonNode? result = frame.Complete();
            active--;
            if (active == 0) return result;
            frames[active - 1].Write(frame.Slot ?? throw new InvalidOperationException("Missing deletion slot."), result);
        }
        throw new InvalidOperationException("Deletion left no result.");

        DeleteFrame Push(JsonNode? node, int first, int last, int depth, JqValueSegment? slot)
        {
            context.Budget.CheckCancellation();
            if (active == frames.Count)
            {
                if (frames.Count == frames.Capacity)
                {
                    int previous = frames.Capacity;
                    int capacity = JqBudget.BufferCapacity((long)frames.Count + 1, Math.Max(4, 2L * previous));
                    storage.ReserveBytes(8L * capacity, 1);
                    context.Budget.CheckCancellation();
                    frames.Capacity = capacity;
                    if (previous != 0) storage.DiscardBytes(8L * previous);
                }
                storage.ReserveBytes(256, 1);
                context.Budget.CheckCancellation();
                frames.Add(new DeleteFrame(context, storage));
            }
            DeleteFrame frame = frames[active++];
            frame.Reset(node, first, last, depth, slot);
            return frame;
        }
    }

    private static bool TrySlice(JqValueSegment key, out double? start, out double? end)
    {
        if (key is SliceSegment slice)
        {
            start = slice.Start;
            end = slice.End;
            return true;
        }
        if (key is SliceObjectSegment raw)
        {
            if (!JqPaths.TryGetSliceObjectBounds(raw.Raw, out start, out end))
                throw new JqException("Array/string slice indices must be integers");
            return true;
        }
        start = null;
        end = null;
        return false;
    }

    // Frames and their backing capacities are reused at each ancestor depth.
    // Child ranges refer to the one sorted vector; slice views borrow references.
    private sealed class DeleteFrame(JqContext context, JqStorage storage)
    {
        internal JsonNode? Node;
        internal int Next;
        internal int Last;
        internal int Depth;
        internal JqValueSegment? Slot;
        private IList<JsonNode?>? _source;
        private int _offset;
        private int _count;
        private bool _slice;
        private bool _written;
        private List<JsonNode?>? _items;
        private List<JqValueSegment>? _removals;
        private Dictionary<string, Change>? _changes;
        private int _changeCapacity;
        private bool[] _removed = [];

        internal bool IsArray => _source is not null;
        internal int Count => _written ? Items.Count : _count;
        private List<JsonNode?> Items => _items ?? throw new InvalidOperationException("Missing deletion array.");
        private JsonNode? At(int index) => _written ? Items[index] : _source?[_offset + index];

        internal void Reset(JsonNode? node, int first, int last, int depth, JqValueSegment? slot)
        {
            Node = node;
            Next = first;
            Last = last;
            Depth = depth;
            Slot = slot;
            _source = node as JsonArray;
            _offset = 0;
            _count = (node as JsonArray)?.Count ?? 0;
            _slice = false;
            _written = false;
            _items?.Clear();
            _removals?.Clear();
            _changes?.Clear();
        }

        internal void BorrowSlice(DeleteFrame parent, int from, int to)
        {
            _source = parent._written ? parent.Items : parent._source;
            _offset = (parent._written ? 0 : parent._offset) + from;
            _count = to - from;
            _slice = true;
        }

        internal JsonNode? Read(JqValueSegment key)
        {
            key = JqPaths.TruncateFractional(key);
            if (Node is JsonObject obj && key is KeySegment name)
                return _changes is not null && _changes.TryGetValue(name.Key, out Change change) ? change.Value
                    : obj.TryGetPropertyValue(name.Key, out JsonNode? child) ? child : null;
            if (IsArray)
            {
                if (key is IndexSegment index)
                {
                    long at = index.Index < 0 ? Count + index.Index : index.Index;
                    return !index.IsNaN && at >= 0 && at < Count ? At((int)at) : null;
                }
                if (key is InvalidSegment)
                    throw new JqException("expected a number for indexing an array but got: " + Render(key));
            }
            return JqPathReads.GetPath(Node, new[] { key }, context);
        }

        internal void Write(JqValueSegment key, JsonNode? value)
        {
            context.Budget.CheckCancellation();
            key = JqPaths.TruncateFractional(key);
            if (Node is JsonObject && key is KeySegment name)
            {
                ChangeObject(name.Key, new Change(value, false));
                return;
            }
            if (IsArray)
            {
                EnsureItems(Count);
                if (key is IndexSegment index)
                {
                    long at = index.Index < 0 ? Items.Count + index.Index : index.Index;
                    Items[(int)at] = value;
                    return;
                }
                if (TrySlice(key, out double? start, out double? end))
                {
                    if (value is not JsonArray pieces)
                        throw new JqException("A slice of an array can only be assigned another array");
                    JqPaths.ResolveSlice(Items.Count, start, end, out int from, out int to);
                    EnsureItems(checked(Items.Count - (to - from) + pieces.Count));
                    Items.RemoveRange(from, to - from);
                    Items.InsertRange(from, pieces);
                    // Completed slice views are private results; transfer children.
                    pieces.Clear();
                    return;
                }
            }
            throw new JqException("Cannot update string slices");
        }

        internal void Remove(JqValueSegment key)
        {
            context.Budget.CheckCancellation();
            if (_removals is null) { storage.ReserveBytes(32, 1); context.Budget.CheckCancellation(); _removals = []; }
            if (_removals.Count == _removals.Capacity)
            {
                int previous = _removals.Capacity;
                int capacity = JqBudget.BufferCapacity((long)_removals.Count + 1, Math.Max(4, 2L * previous));
                storage.ReserveBytes(8L * capacity, 1);
                context.Budget.CheckCancellation();
                _removals.Capacity = capacity;
                if (previous != 0) storage.DiscardBytes(8L * previous);
            }
            _removals.Add(key);
        }

        internal JsonNode? Complete()
        {
            bool changed = _written || _changes is { Count: > 0 };
            if (_removals is { Count: > 0 } removals && Node is not null)
            {
                if (IsArray)
                {
                    if (_removed.Length < Count)
                    {
                        storage.ReserveBytes(Count, 1);
                        context.Budget.CheckCancellation();
                        int previous = _removed.Length;
                        _removed = new bool[Count];
                        if (previous != 0) storage.DiscardBytes(previous);
                    }
                    Array.Clear(_removed, 0, Count);
                    foreach (JqValueSegment key in removals)
                    {
                        context.Budget.CheckCancellation();
                        if (key is IndexSegment index)
                        {
                            long at = index.Index < 0 ? Count + index.Index : index.Index;
                            if (!index.IsNaN && at >= 0 && at < Count) { _removed[(int)at] = true; changed = true; }
                        }
                        else if (key is FractionalSegment fractional)
                        {
                            long at = fractional.Value < 0 ? Count + JqPaths.TruncateIndex(fractional.Value) : JqPaths.TruncateIndex(fractional.Value);
                            if (at >= 0 && at < Count) { _removed[(int)at] = true; changed = true; }
                        }
                        else if (TrySlice(key, out double? start, out double? end))
                        {
                            JqPaths.ResolveSlice(Count, start, end, out int from, out int to);
                            for (int at = from; at < to; at++) { context.Budget.CheckCancellation(); _removed[at] = true; changed = true; }
                        }
                        else throw new JqException("expected a number for indexing an array but got: " + Render(key));
                    }
                }
                else if (Node is JsonObject)
                {
                    foreach (JqValueSegment key in removals)
                    {
                        context.Budget.CheckCancellation();
                        if (key is KeySegment name) ChangeObject(name.Key, new Change(null, true));
                        else if (key is SliceSegment or SliceObjectSegment) throw new JqException("Cannot delete object field of object");
                        else throw new JqException("expected a string for object key but got: " + Render(key));
                    }
                    changed = true;
                }
                else
                {
                    JqValueSegment key = removals[0];
                    if (key is InvalidSegment)
                        throw new JqException("expected a string for object key but got: " + Render(key));
                    throw new JqException("Cannot delete fields from " + JqRuntime.TypeName(Node));
                }
            }
            if (!changed && !_slice) return Node;
            context.Budget.ReserveBytes(64);
            if (IsArray)
            {
                var result = new JsonArray();
                for (int index = 0; index < Count; index++)
                {
                    context.Budget.CheckCancellation();
                    if (_removals is { Count: > 0 } && _removed[index]) continue;
                    JsonNode? value = At(index);
                    context.Budget.ReserveArrayItem(value);
                    result.Add(value?.Parent is null ? value : context.Budget.CopyValue(value, 1));
                }
                return result;
            }
            if (Node is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var property in obj)
                {
                    context.Budget.CheckCancellation();
                    JsonNode? value = property.Value;
                    if (_changes is not null && _changes.TryGetValue(property.Key, out Change change))
                    {
                        if (change.Removed) continue;
                        value = change.Value;
                    }
                    context.Budget.ReserveObjectItem(value);
                    result.Add(property.Key, value?.Parent is null ? value : context.Budget.CopyValue(value, 1));
                }
                return result;
            }
            return Node;
        }

        private string Render(JqValueSegment key) => context.Runtime.TruncatedDump(key.ToJson());

        private void EnsureItems(int required)
        {
            if (_items is null) { storage.ReserveBytes(32, 1); context.Budget.CheckCancellation(); _items = []; }
            if (required > _items.Capacity)
            {
                int previous = _items.Capacity;
                int capacity = JqBudget.BufferCapacity(required, Math.Max(4, 2L * previous));
                storage.ReserveBytes(8L * capacity, 1);
                context.Budget.CheckCancellation();
                _items.Capacity = capacity;
                if (previous != 0) storage.DiscardBytes(8L * previous);
            }
            if (_written) return;
            for (int index = 0; index < _count; index++) { context.Budget.CheckCancellation(); _items.Add(_source?[_offset + index]); }
            _written = true;
        }

        private void ChangeObject(string key, Change change)
        {
            if (_changes is null) { storage.ReserveBytes(64, 1); context.Budget.CheckCancellation(); _changes = new(StringComparer.Ordinal); }
            if (_changes.Count == _changeCapacity && !_changes.ContainsKey(key))
            {
                int requested = JqBudget.BufferCapacity((long)_changes.Count + 1, Math.Max(4, 2L * _changeCapacity));
                // Dictionary prime rounding is below twice the requested size.
                storage.ReserveBytes(128L * requested, 1);
                context.Budget.CheckCancellation();
                int capacity = _changes.EnsureCapacity(requested);
                storage.DiscardBytes(128L * requested - 64L * capacity + 64L * _changeCapacity);
                _changeCapacity = capacity;
            }
            _changes[key] = change;
        }

        private readonly record struct Change(JsonNode? Value, bool Removed);
    }

    // Reuse jq value ordering for raw segments. Two small private objects adapt
    // syntax slices without allocating JSON containers for every comparison.
    private sealed class PathOrder(JqContext context, JqStorage storage) : IComparer<IReadOnlyList<JqValueSegment>>, IDisposable
    {
        private readonly JqComparer _values = new(context.Budget);
        private JsonObject? _left;
        private JsonObject? _right;

        public int Compare(IReadOnlyList<JqValueSegment>? left, IReadOnlyList<JqValueSegment>? right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            int count = Math.Min(left.Count, right.Count);
            for (int index = 0; index < count; index++)
            {
                context.Budget.CheckCancellation();
                JqValueSegment first = JqPaths.UpdateSegment(left[index]);
                JqValueSegment second = JqPaths.UpdateSegment(right[index]);
                int order;
                if (first is IndexSegment a && second is IndexSegment b && !a.IsNaN && !b.IsNaN) order = a.Index.CompareTo(b.Index);
                else if (first is KeySegment x && second is KeySegment y) order = JqComparer.CompareStrings(x.Key, y.Key, context.Budget.CancellationToken);
                else order = _values.Compare(Value(first, ref _left), Value(second, ref _right));
                if (order != 0) return order;
            }
            return left.Count.CompareTo(right.Count);
        }

        internal bool Equal(JqValueSegment left, JqValueSegment right)
        {
            context.Budget.CheckCancellation();
            if (left is IndexSegment a && right is IndexSegment b) return !a.IsNaN && !b.IsNaN && a.Index == b.Index;
            if (left is KeySegment x && right is KeySegment y) return x.Key == y.Key;
            return context.Runtime.JsonEquals(Value(left, ref _left), Value(right, ref _right));
        }

        private JsonNode? Value(JqValueSegment segment, ref JsonObject? synthetic)
        {
            if (segment is not SliceSegment slice) return segment.ToJson();
            if (synthetic is null) { storage.ReserveBytes(384, 1); context.Budget.CheckCancellation(); synthetic = new JsonObject(); }
            synthetic["start"] = slice.Start is { } start ? JqRuntime.CreateNumber(start) : null;
            synthetic["end"] = slice.End is { } end ? JqRuntime.CreateNumber(end) : null;
            return synthetic;
        }

        public void Dispose() => _values.Dispose();
    }
}

// `path(EXPR)`: enumerates tracked segments as JSON arrays.
internal sealed class PathBuiltinFilter(JqFilter Paths) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var root = new JqValuePath(new List<JqValueSegment>(), input, true);
        await foreach (JqValuePath pair in Paths.EvaluatePaths(root, context, environment).ConfigureAwait(false))
        {
            if (!pair.Tracked)
                throw new JqException(JqPaths.InvalidResult(pair.Value, context));
            yield return JqPaths.PathToJson(pair.Segments, context.Budget);
        }
    }
}

// `del(EXPRS...)`: collects paths from every argument, then deletes once.
internal sealed class DelBuiltinFilter(IReadOnlyList<JqFilter> Args) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var collected = new List<IReadOnlyList<JqValueSegment>>();
        foreach (JqFilter paths in Args)
            collected.AddRange(await JqPaths.CollectPaths(paths, input, context, environment).ConfigureAwait(false));
        yield return JqPathDeletes.DeletePaths(input, collected, context);
    }
}

// `getpath(PATHS)`: reads through a path value.
internal sealed class GetpathBuiltinFilter(JqFilter Paths) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? paths in Paths.Evaluate(input, context, environment).ConfigureAwait(false))
            yield return JqPathReads.GetPath(input, JqPaths.ParsePathValue(paths, context), context);
    }

    protected override async IAsyncEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        // Path transparency mirrors upstream _jq_path_append: a tracked input
        // keeps tracking with the path argument appended, so path(getpath($p)),
        // getpath($p) = value, and getpath($p) |= update all resolve through $p.
        // Fresh inputs travel untracked and fail at the next path boundary.
        await foreach (JsonNode? paths in Paths.Evaluate(pair.Value, context, environment).ConfigureAwait(false))
        {
            List<JqValueSegment> segments = JqPaths.ParsePathValue(paths, context);
            JsonNode? value = JqPathReads.GetPath(pair.Value, segments, context);
            if (!pair.Tracked)
            {
                yield return pair.WithValue(value, false);
                continue;
            }
            context.Budget.ReserveBytes(24L * (pair.Segments.Count + segments.Count));
            var extended = new List<JqValueSegment>(pair.Segments);
            extended.AddRange(segments);
            yield return new JqValuePath(extended, value, true);
        }
    }
}

// `setpath(PATHS; VALUES)`: cartesian value-outer combinations.
internal sealed class SetpathBuiltinFilter(JqFilter Paths, JqFilter Values) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? value in Values.Evaluate(input, context, environment).ConfigureAwait(false))
            await foreach (JsonNode? paths in Paths.Evaluate(input, context, environment).ConfigureAwait(false))
                yield return JqPathUpdates.SetPath(input, JqPaths.ParsePathValue(paths, context), value, context);
    }
}

// `delpaths(PATHS)`: deletes a value holding an array of paths.
internal sealed class DelpathsBuiltinFilter(JqFilter Paths) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        await foreach (JsonNode? paths in Paths.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (paths is not JsonArray array)
                throw new JqException("Paths must be specified as an array");
            if (array.Count > JqPaths.MaxPathDepth)
                throw new JqException("Path too deep");
            var collected = new List<IReadOnlyList<JqValueSegment>>();
            foreach (JsonNode? element in array)
            {
                if (element is not JsonArray)
                    throw new JqException("Path must be specified as array, not " + JqRuntime.TypeName(element));
                collected.Add(JqPaths.ParsePathValue(element, context));
            }
            yield return JqPathDeletes.DeletePaths(input, collected, context);
        }
    }
}

// `pick(EXPRS...)`: rebuilds from null with the original values at each path.
internal sealed class PickFilter(IReadOnlyList<JqFilter> Args) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JsonNode? state = null;
        foreach (JqFilter paths in Args)
            foreach (IReadOnlyList<JqValueSegment> segments in await JqPaths.CollectPaths(paths, input, context, environment).ConfigureAwait(false))
                state = JqPathUpdates.SetPath(state, segments, JqPathReads.GetPath(input, segments, context), context);
        yield return state;
    }
}

// Assignment and update operators. Paths enumerate once from the original
// input; right-hand values stream per combination with values outermost.
// `|=` threads evolving state through first-only updates and deletes paths
// whose update yields nothing.
internal sealed class AssignFilter(string Op, JqFilter Paths, JqFilter Values) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        List<IReadOnlyList<JqValueSegment>> paths = await JqPaths.CollectPaths(Paths, input, context, environment).ConfigureAwait(false);
        if (Op == "|=")
        {
            yield return await ModifyLoop(paths, Values, input, context, environment).ConfigureAwait(false);
            yield break;
        }
        await foreach (JsonNode? value in Values.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            JsonNode? state = input;
            foreach (IReadOnlyList<JqValueSegment> segments in paths)
            {
                context.Budget.CheckCancellation();
                JsonNode? replacement = value;
                if (Op != "=")
                {
                    JsonNode? current = JqPathReads.GetPath(state, segments, context);
                    replacement = Op == "//=" ? (Truthy(current) ? current : value)
                        : BinaryFilter.Apply(context, Op switch
                        {
                            "+=" => "+", "-=" => "-", "*=" => "*", "/=" => "/", "%=" => "%",
                            _ => throw new JqException($"unsupported assignment {Op}")
                        }, current, value);
                }
                state = JqPathUpdates.SetPath(state, segments, replacement, context);
            }
            yield return state;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<JsonNode?> ModifyLoop(IReadOnlyList<IReadOnlyList<JqValueSegment>> paths, JqFilter update, JsonNode? input, JqContext context, JqEnvironment environment)
    {
        JsonNode? state = input;
        var deleted = new List<IReadOnlyList<JqValueSegment>>();
        foreach (IReadOnlyList<JqValueSegment> segments in paths)
        {
            bool ran = false;
            var results = update.Evaluate(JqPathReads.GetPath(state, segments, context), context, environment).GetAsyncEnumerator();
            await using var resultsLifetime = results.ConfigureAwait(false);
            while (await results.MoveNextAsync().ConfigureAwait(false))
            {
                state = JqPathUpdates.SetPath(state, segments, results.Current, context);
                ran = true;
                break;
            }
            if (!ran)
                deleted.Add(segments);
        }
        return JqPathDeletes.DeletePaths(state, deleted, context);
    }
}

// `_modify(paths; update)`: the parser-support helper behind `|=`, threading
// an update filter through first-only state from the enumerated paths with
// empty-update deletion, exactly like the reference builtin.jq definition.
internal sealed class ModifyBuiltinFilter(JqFilter Paths, JqFilter Update) : JqFilter
{

    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        List<IReadOnlyList<JqValueSegment>> paths = await JqPaths.CollectPaths(Paths, input, context, environment).ConfigureAwait(false);
        yield return await AssignFilter.ModifyLoop(paths, Update, input, context, environment).ConfigureAwait(false);
    }
}
