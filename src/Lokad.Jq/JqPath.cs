using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// Path segments for `path`, assignment, and update machinery. Indices stay
// raw (negative values resolve against the live container at use time);
// slices keep optional raw bounds with the same resolution.
internal abstract record JqValueSegment
{
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

// A segment that can never address a container (booleans, arrays, and nulls
// from path values). Traversal reports container-shaped type errors.
internal sealed record InvalidSegment(JsonNode? Raw) : JqValueSegment
{
    internal override JsonNode? ToJson() => Raw?.DeepClone();
}

// One enumerated path: accumulated segments, the value found there, and
// whether the value survived intact (fresh values travel untracked and fail
// at the next path boundary).
internal sealed record JqValuePath(IReadOnlyList<JqValueSegment> Segments, JsonNode? Value, bool Tracked);

internal static class JqPaths
{
    internal const int MaxPathDepth = 10000;
    internal const int MaxArrayPad = 1_000_000;

    internal static string InvalidResult(JsonNode? value, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return "Invalid path expression with result " + context.Runtime.Serialize(value, false, null, false);
    }

    internal static string InvalidAccess(JsonNode? segment, JsonNode? container, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return "Invalid path expression near attempt to access element "
            + context.Runtime.Serialize(segment, false, null, false)
            + " of "
            + context.Runtime.Serialize(container, false, null, false);
    }

    internal static string InvalidIterate(JsonNode? value, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return "Invalid path expression near attempt to iterate through " + context.Runtime.Serialize(value, false, null, false);
    }

    // Collects tracked segments; fresh values fail exactly like upstream.
    internal static List<IReadOnlyList<JqValueSegment>> CollectPaths(JqFilter paths, JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var collected = new List<IReadOnlyList<JqValueSegment>>();
        foreach (JqValuePath pair in paths.EvaluatePaths(new JqValuePath(new List<JqValueSegment>(), input, true), context, environment))
        {
            if (!pair.Tracked)
                throw new JqException(InvalidResult(pair.Value, context));
            collected.Add(pair.Segments);
        }
        return collected;
    }

    internal static IReadOnlyList<JqValueSegment> Extend(IReadOnlyList<JqValueSegment> segments, JqValueSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(segment);
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

    internal static JsonNode PathToJson(IReadOnlyList<JqValueSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var path = new JsonArray();
        foreach (JqValueSegment segment in segments)
            path.Add(segment.ToJson());
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
        var segments = new List<JqValueSegment>();
        foreach (JsonNode? element in array)
        {
            if (TryGetString(element, out string? key))
                segments.Add(new KeySegment(key));
            else if (TryGetIndex(element, out long index, out bool isNaN))
                segments.Add(new IndexSegment(index, isNaN));
            else if (element is JsonObject slice)
                segments.Add(new SliceSegment(ParseBound(slice, "start"), ParseBound(slice, "end")));
            else
                segments.Add(new InvalidSegment(element?.DeepClone()));
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

    private static double ClampDouble(double value)
    {
        if (value < long.MinValue) return long.MinValue;
        if (value > long.MaxValue) return long.MaxValue;
        return value;
    }

    private static double? ParseBound(JsonObject slice, string name)
    {
        if (!slice.TryGetPropertyValue(name, out JsonNode? bound) || bound is null)
            return null;
        if (bound is JsonValue small && small.TryGetValue<int>(out int directInt))
            return directInt;
        if (bound is JsonValue whole && whole.TryGetValue<long>(out long direct))
            return direct;
        if (bound is JsonValue real && real.TryGetValue<double>(out double value))
            return double.IsNaN(value) ? null : value;
        throw new JqException("invalid slice bounds in path");
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

// Persistent path updates live beside the segment model. Every container on
// the spine is rebuilt (children shared copy-on-write); nothing is ever
// mutated in place, so sibling outputs can never observe one another.
internal static class JqPathUpdates
{
    private sealed record Frame(JqValueSegment Segment, JsonNode Container, int Index, int From, int To);

    internal static JsonNode? SetPath(JsonNode? root, IReadOnlyList<JqValueSegment> segments, JsonNode? value, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(context);
        if (segments.Count > JqPaths.MaxPathDepth)
            throw new JqException("Path too deep");
        if (segments.Count == 0)
            return context.Runtime.Clone(value);
        var frames = new Stack<Frame>();
        JsonNode? current = root;
        for (int depth = 0; depth < segments.Count; depth++)
            current = Descend(current, segments[depth], frames, context);
        JsonNode? leaf = context.Runtime.Clone(value);
        while (frames.TryPop(out Frame? frame))
            leaf = ApplyFrame(frame, leaf, context);
        return leaf;
    }

    private static JsonNode? Descend(JsonNode? current, JqValueSegment segment, Stack<Frame> frames, JqContext context)
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
                    frames.Push(new Frame(segment, CloneObject(obj, context), 0, 0, 0));
                    return obj.TryGetPropertyValue(key.Key, out JsonNode? child) ? child : null;
                }
                throw new JqException("expected an object but got: " + context.Runtime.Serialize(current, false, null, false));
            case IndexSegment index:
                if (index.IsNaN)
                    throw new JqException("Cannot set array element at NaN index");
                if (current is null)
                {
                    if (index.Index < 0)
                        throw new JqException("Out of bounds negative array index");
                    if (index.Index > JqPaths.MaxArrayPad)
                        throw new JqException("Array index too large");
                    frames.Push(new Frame(segment, PaddedArray(null, index.Index, context), (int)index.Index, 0, 0));
                    return null;
                }
                if (current is JsonArray arr)
                {
                    long resolved = index.Index < 0 ? arr.Count + index.Index : index.Index;
                    if (resolved < 0)
                        throw new JqException("Out of bounds negative array index");
                    if (resolved > JqPaths.MaxArrayPad)
                        throw new JqException("Array index too large");
                    frames.Push(new Frame(segment, PaddedArray(arr, resolved, context), (int)resolved, 0, 0));
                    return resolved < arr.Count ? arr[(int)resolved] : null;
                }
                // Like the reference probe read inside jv_setpath, indexing a
                // non-array reports the container and key kinds.
                throw new JqException("Cannot index " + JqRuntime.TypeName(current) + " with number (" + index.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            case SliceSegment slice:
                if (current is null)
                {
                    var fresh = new JsonArray();
                    context.Budget.ChargeNode();
                    frames.Push(new Frame(segment, fresh, 0, 0, 0));
                    return SliceCopy(fresh, 0, 0, context);
                }
                if (current is JsonArray target)
                {
                    JqPaths.ResolveSlice(target.Count, slice.Start, slice.End, out int from, out int to);
                    frames.Push(new Frame(segment, CloneArray(target, context), 0, from, to));
                    return SliceCopy(target, from, to, context);
                }
                if (current is JsonValue scalar && scalar.TryGetValue<string>(out _))
                    throw new JqException("Cannot update string slices");
                throw new JqException("expected an array but got: " + context.Runtime.Serialize(current, false, null, false));
            default:
                throw SegmentTypeError(current, segment, context);
        }
    }

    private static JsonNode ApplyFrame(Frame frame, JsonNode? leaf, JqContext context)
    {
        switch (frame.Segment)
        {
            case KeySegment key when frame.Container is JsonObject obj:
                obj[key.Key] = leaf;
                return obj;
            case IndexSegment when frame.Container is JsonArray arr:
                arr[frame.Index] = leaf;
                return arr;
            case SliceSegment when frame.Container is JsonArray arr:
                if (leaf is not JsonArray pieces)
                    throw new JqException("A slice of an array can only be assigned another array");
                var rebuilt = new JsonArray();
                context.Budget.ChargeNode();
                for (int index = 0; index < frame.From; index++)
                    rebuilt.Add(arr[index]?.DeepClone());
                foreach (JsonNode? piece in pieces)
                    rebuilt.Add(piece?.DeepClone());
                for (int index = frame.To; index < arr.Count; index++)
                    rebuilt.Add(arr[index]?.DeepClone());
                return rebuilt;
            default:
                throw new InvalidOperationException("Mismatched path frame.");
        }
    }
    private static JsonObject NewObject(JqContext context)
    {
        context.Budget.ChargeNode();
        return new JsonObject();
    }

    private static JsonObject CloneObject(JsonObject source, JqContext context)
    {
        // Charge the whole subtree like Clone does: per-level deep copies
        // would otherwise allocate quadratically against deep inputs.
        context.Budget.ChargeTree(source);
        var clone = new JsonObject();
        foreach (var property in source)
            clone.Add(property.Key, property.Value?.DeepClone());
        return clone;
    }

    private static JsonArray CloneArray(JsonArray source, JqContext context)
    {
        // Charge the whole subtree like Clone does: per-level deep copies
        // would otherwise allocate quadratically against deep inputs.
        context.Budget.ChargeTree(source);
        var clone = new JsonArray();
        foreach (JsonNode? child in source)
            clone.Add(child?.DeepClone());
        return clone;
    }

    // Pads through the resolved index so frame application always lands
    // in range; every pad element is budget-charged.
    private static JsonArray PaddedArray(JsonArray? source, long resolved, JqContext context)
    {
        var padded = new JsonArray();
        if (source is not null)
        {
            // Charge the whole subtree like Clone does: per-level deep copies
            // would otherwise allocate quadratically against deep inputs.
            context.Budget.ChargeTree(source);
            foreach (JsonNode? child in source)
                padded.Add(child?.DeepClone());
        }
        else
        {
            context.Budget.ChargeNode();
        }
        while (padded.Count <= resolved)
        {
            context.Budget.ChargeNode();
            padded.Add(null);
        }
        return padded;
    }

    private static JsonArray SliceCopy(JsonArray source, int from, int to, JqContext context)
    {
        var slice = new JsonArray();
        // Charge the whole subtree like Clone does: per-level deep copies
        // would otherwise allocate quadratically against deep inputs.
        context.Budget.ChargeTree(source);
        for (int index = from; index < to; index++)
            slice.Add(source[index]?.DeepClone());
        return slice;
    }

    private static JqException SegmentTypeError(JsonNode? current, JqValueSegment segment, JqContext context)
    {
        string raw = context.Runtime.Serialize(segment.ToJson(), false, null, false);
        if (current is JsonArray)
            return new JqException("expected a number for indexing an array but got: " + raw);
        return new JqException("expected a string for object key but got: " + raw);
    }
}

internal static class JqPathReads
{
    // Read traversal mirroring value semantics: missing reads null,
    // mistyped containers raise the same errors as ordinary reads.
    internal static JsonNode? GetPath(JsonNode? root, IReadOnlyList<JqValueSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        JsonNode? current = root;
        foreach (JqValueSegment segment in segments)
        {
            switch (segment)
            {
                case KeySegment key:
                    if (current is JsonObject obj)
                        current = obj.TryGetPropertyValue(key.Key, out JsonNode? child) ? child : null;
                    else if (current is null)
                        current = null;
                    else
                        throw new JqRuntimeException("Cannot index " + JqRuntime.TypeName(current) + " with string (" + System.Text.Json.Nodes.JsonValue.Create(key.Key).ToJsonString() + ")");
                    break;
                case IndexSegment index:
                    if (current is JsonArray arr)
                    {
                        if (index.IsNaN)
                            current = null;
                        else
                        {
                            long resolved = index.Index < 0 ? arr.Count + index.Index : index.Index;
                            current = resolved >= 0 && resolved < arr.Count ? arr[(int)resolved] : null;
                        }
                    }
                    else if (current is null)
                        current = null;
                    else
                        throw new JqRuntimeException("Cannot index " + JqRuntime.TypeName(current) + " with number (" + (index.IsNaN ? "NaN" : index.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")");
                    break;
                case SliceSegment slice:
                    if (current is JsonArray array)
                    {
                        JqPaths.ResolveSlice(array.Count, slice.Start, slice.End, out int from, out int to);
                        var part = new JsonArray();
                        for (int position = from; position < to; position++)
                            part.Add(array[position]?.DeepClone());
                        current = part;
                    }
                    else if (current is JsonValue scalar && scalar.TryGetValue<string>(out string? text) && text is not null)
                    {
                        var runes = new List<System.Text.Rune>();
                        foreach (var rune in text.EnumerateRunes())
                            runes.Add(rune);
                        JqPaths.ResolveSlice(runes.Count, slice.Start, slice.End, out int from, out int to);
                        var builder = new System.Text.StringBuilder();
                        for (int position = from; position < to; position++)
                            builder.Append(runes[position].ToString());
                        current = JsonValue.Create(builder.ToString());
                    }
                    else if (current is null)
                        current = null;
                    else
                        throw new JqRuntimeException($"cannot slice {JqRuntime.TypeName(current)}");
                    break;
                default:
                    throw MismatchError(current, segment);
            }
        }
        return current;
    }

    private static JqException MismatchError(JsonNode? current, JqValueSegment segment)
    {
        string raw = segment.ToJson()?.ToJsonString() ?? "null";
        if (current is JsonArray)
            return new JqException("expected a number for indexing an array but got: " + raw);
        return new JqException("expected a string for object key but got: " + raw);
    }
}
internal static class JqPathDeletes
{
    private sealed record Group(IReadOnlyList<JqValueSegment> Path, int Start, int End);

    // Sorted grouped deletion: whole keys drop at once (so array indices
    // never shift mid-run), deeper paths recurse. Missing containers skip;
    // mistyped primitives raise ordinary read errors.
    internal static JsonNode? DeletePaths(JsonNode? root, IReadOnlyList<IReadOnlyList<JqValueSegment>> paths, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(context);
        var effective = new List<IReadOnlyList<JqValueSegment>>();
        foreach (var path in paths)
        {
            if (path.Count == 0)
                return null;
            bool usable = true;
            foreach (var segment in path)
                if (segment is IndexSegment index && index.IsNaN)
                    usable = false;
            if (usable)
                effective.Add(path);
        }
        if (effective.Count == 0)
            return root;
        effective.Sort(ComparePaths);
        return DeleteGrouped(root, effective, 0, context);
    }

    private static int ComparePaths(IReadOnlyList<JqValueSegment>? left, IReadOnlyList<JqValueSegment>? right)
    {
        int count = Math.Min(left?.Count ?? 0, right?.Count ?? 0);
        for (int index = 0; index < count; index++)
        {
            int order = CompareSegment(left![index], right![index]);
            if (order != 0)
                return order;
        }
        return (left?.Count ?? 0).CompareTo(right?.Count ?? 0);
    }

    private static int CompareSegment(JqValueSegment left, JqValueSegment right)
    {
        int rank = Rank(left).CompareTo(Rank(right));
        if (rank != 0)
            return rank;
        if (left is IndexSegment li && right is IndexSegment ri)
            return li.Index.CompareTo(ri.Index);
        if (left is KeySegment lk && right is KeySegment rk)
            return string.Compare(lk.Key, rk.Key, StringComparison.Ordinal);
        if (left is SliceSegment ls && right is SliceSegment rs)
        {
            int start = Nullable.Compare(ls.Start, rs.Start);
            return start != 0 ? start : Nullable.Compare(ls.End, rs.End);
        }
        return 0;
    }

    private static int Rank(JqValueSegment segment) => segment switch
    {
        IndexSegment => 0,
        KeySegment => 1,
        SliceSegment => 2,
        _ => 3,
    };

    // Heap-allocated frame for iterative grouped deletion. Deletion depth is path
    // length, which the value budget does not bound below a CLR stack overflow, so
    // nested call frames are not an option. A frame resumes exactly where a child
    // call would return, preserving group order and error precedence.
    private sealed class DeleteFrame(JsonNode? node, List<IReadOnlyList<JqValueSegment>> paths, int depth)
    {
        public JsonNode? Node = node;
        public List<IReadOnlyList<JqValueSegment>> Paths = paths;
        public int Depth = depth;
        public JqValueSegment? Slot;
        public int First;
        public List<(JqValueSegment Key, JsonNode? Value)> Replacements = new();
        public List<JqValueSegment> Removals = new();
        public JsonArray? SliceArray;
        public List<IReadOnlyList<JqValueSegment>> SliceDeeper = new();
        public int SliceTo;
        public int SlicePos = -1;
        public JsonNode? Result;
    }

    private static JsonNode? DeleteGrouped(JsonNode? root, List<IReadOnlyList<JqValueSegment>> paths, int depth, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(context);
        var stack = new Stack<DeleteFrame>();
        stack.Push(new DeleteFrame(root, paths, depth));
        while (stack.Count > 0)
        {
            context.Budget.CheckCancellation();
            DeleteFrame frame = stack.Peek();
            if (!AdvanceDeleteFrame(frame, stack, context))
                continue;
            stack.Pop();
            if (stack.Count == 0)
                return frame.Result;
            JqValueSegment? slot = frame.Slot;
            if (slot is null)
                throw new InvalidOperationException("Grouped deletion lost its result slot.");
            stack.Peek().Replacements.Add((slot, frame.Result));
        }
        throw new InvalidOperationException("Grouped deletion left no result.");
    }

    // Runs one frame until it needs a child result or finishes. Returns true with
    // Result set when the frame is complete.
    private static bool AdvanceDeleteFrame(DeleteFrame frame, Stack<DeleteFrame> stack, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(context);
        if (frame.SlicePos >= 0)
        {
            JsonArray? sliced = frame.SliceArray;
            if (sliced is null)
                throw new InvalidOperationException("Grouped deletion lost its slice target.");
            if (frame.SlicePos < frame.SliceTo)
            {
                int position = frame.SlicePos;
                frame.SlicePos++;
                stack.Push(new DeleteFrame(sliced[position], frame.SliceDeeper, frame.Depth + 1) { Slot = new IndexSegment(position, false) });
                return false;
            }
            frame.SlicePos = -1;
        }
        while (frame.First < frame.Paths.Count)
        {
            int first = frame.First;
            int last = first + 1;
            while (last < frame.Paths.Count && CompareSegment(frame.Paths[last][frame.Depth], frame.Paths[first][frame.Depth]) == 0)
                last++;
            JqValueSegment key = frame.Paths[first][frame.Depth];
            if (key is SliceSegment range && frame.Node is JsonArray targets)
            {
                JqPaths.ResolveSlice(targets.Count, range.Start, range.End, out int from, out int to);
                var deeper = new List<IReadOnlyList<JqValueSegment>>();
                for (int index = first; index < last; index++)
                    if (frame.Paths[index].Count > frame.Depth + 1)
                        deeper.Add(frame.Paths[index]);
                frame.First = last;
                if (deeper.Count == 0)
                {
                    for (int position = from; position < to; position++)
                        frame.Removals.Add(new IndexSegment(position, false));
                    continue;
                }
                if (from >= to)
                    continue;
                frame.SliceArray = targets;
                frame.SliceDeeper = deeper;
                frame.SliceTo = to;
                frame.SlicePos = from + 1;
                stack.Push(new DeleteFrame(targets[from], deeper, frame.Depth + 1) { Slot = new IndexSegment(from, false) });
                return false;
            }
            bool whole = false;
            for (int index = first; index < last; index++)
                if (frame.Paths[index].Count == frame.Depth + 1)
                    whole = true;
            if (whole)
            {
                frame.Removals.Add(key);
                frame.First = last;
                continue;
            }
            if (TryGetChild(frame.Node, key, out JsonNode? child, out bool missing))
            {
                if (!missing && child is not null)
                {
                    var subpaths = new List<IReadOnlyList<JqValueSegment>>();
                    for (int index = first; index < last; index++)
                        subpaths.Add(frame.Paths[index]);
                    frame.First = last;
                    stack.Push(new DeleteFrame(child, subpaths, frame.Depth + 1) { Slot = key });
                    return false;
                }
            }
            frame.First = last;
        }
        frame.Result = Rebuild(frame.Node, frame.Replacements, frame.Removals, context);
        return true;
    }
    private static bool TryGetChild(JsonNode? node, JqValueSegment key, out JsonNode? child, out bool missing)
    {
        child = null;
        missing = true;
        switch (key)
        {
            case KeySegment name:
                if (node is JsonObject obj)
                {
                    if (obj.TryGetPropertyValue(name.Key, out JsonNode? existing))
                    {
                        child = existing;
                        missing = false;
                    }
                    return true;
                }
                if (node is null || node is JsonArray)
                    return node is null;
                throw new JqRuntimeException($"cannot index {JqRuntime.TypeName(node)} with string \"{name.Key}\"");
            case IndexSegment index:
                if (node is JsonArray arr)
                {
                    long resolved = index.Index < 0 ? arr.Count + index.Index : index.Index;
                    if (resolved >= 0 && resolved < arr.Count)
                    {
                        child = arr[(int)resolved];
                        missing = false;
                    }
                    return true;
                }
                if (node is null || node is JsonObject)
                    return node is null;
                throw new JqRuntimeException($"cannot index {JqRuntime.TypeName(node)}");
            case SliceSegment:
                return node is null;
            default:
                if (node is null)
                    return false;
                if (node is JsonArray)
                    throw new JqException("expected a number for indexing an array but got: " + key.ToJson()?.ToJsonString());
                throw new JqException("expected a string for object key but got: " + key.ToJson()?.ToJsonString());
        }
    }

    private static JsonNode? Rebuild(JsonNode? node, List<(JqValueSegment Key, JsonNode? Value)> replacements, List<JqValueSegment> removals, JqContext context)
    {
        if (node is JsonObject obj)
        {
            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var removal in removals)
                if (removal is KeySegment name)
                    removed.Add(name.Key);
            var replaced = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (key, value) in replacements)
                if (key is KeySegment name)
                    replaced[name.Key] = value;
            if (removed.Count == 0 && replaced.Count == 0)
                return node;
            context.Budget.ChargeNode();
            var rebuilt = new JsonObject();
            foreach (var property in obj)
            {
                if (removed.Contains(property.Key))
                    continue;
                rebuilt.Add(property.Key, replaced.TryGetValue(property.Key, out JsonNode? next) ? next : property.Value?.DeepClone());
            }
            foreach (var entry in replaced)
                if (!rebuilt.ContainsKey(entry.Key))
                    rebuilt.Add(entry.Key, entry.Value);
            return rebuilt;
        }
        if (node is JsonArray arr)
        {
            var removed = new HashSet<int>();
            foreach (var removal in removals)
                if (removal is IndexSegment index && !index.IsNaN)
                {
                    long resolved = index.Index < 0 ? arr.Count + index.Index : index.Index;
                    if (resolved >= 0 && resolved < arr.Count)
                        removed.Add((int)resolved);
                }
            var replaced = new Dictionary<int, JsonNode?>();
            foreach (var (key, value) in replacements)
                if (key is IndexSegment index && !index.IsNaN)
                {
                    long resolved = index.Index < 0 ? arr.Count + index.Index : index.Index;
                    if (resolved >= 0 && resolved < arr.Count)
                        replaced[(int)resolved] = value;
                }
            if (removed.Count == 0 && replaced.Count == 0)
                return node;
            context.Budget.ChargeNode();
            var rebuilt = new JsonArray();
            for (int position = 0; position < arr.Count; position++)
            {
                if (removed.Contains(position))
                    continue;
                rebuilt.Add(replaced.TryGetValue(position, out JsonNode? next) ? next : arr[position]?.DeepClone());
            }
            return rebuilt;
        }
        return node;
    }
}

// `path(EXPR)`: enumerates tracked segments as JSON arrays.
internal sealed class PathBuiltinFilter(JqFilter Paths) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var root = new JqValuePath(new List<JqValueSegment>(), input, true);
        foreach (JqValuePath pair in Paths.EvaluatePaths(root, context, environment))
        {
            if (!pair.Tracked)
                throw new JqException(JqPaths.InvalidResult(pair.Value, context));
            yield return JqPaths.PathToJson(pair.Segments);
        }
    }
}

// `del(EXPRS...)`: collects paths from every argument, then deletes once.
internal sealed class DelBuiltinFilter(IReadOnlyList<JqFilter> Args) : JqFilter
{
    // Deletion rebuilds the root, so results travel untracked.
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var collected = new List<IReadOnlyList<JqValueSegment>>();
        foreach (JqFilter paths in Args)
            collected.AddRange(JqPaths.CollectPaths(paths, input, context, environment));
        yield return JqPathDeletes.DeletePaths(input, collected, context);
    }
}

// `getpath(PATHS)`: reads through a path value.
internal sealed class GetpathBuiltinFilter(JqFilter Paths) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? paths in Paths.Evaluate(input, context, environment))
            yield return context.Runtime.Clone(JqPathReads.GetPath(input, JqPaths.ParsePathValue(paths, context)));
    }

    protected override IEnumerable<JqValuePath> EvaluatePathsCore(JqValuePath pair, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        // Path transparency mirrors upstream _jq_path_append: a tracked input
        // keeps tracking with the path argument appended, so path(getpath($p)),
        // getpath($p) = value, and getpath($p) |= update all resolve through $p.
        // Fresh inputs travel untracked and fail at the next path boundary.
        foreach (JsonNode? paths in Paths.Evaluate(pair.Value, context, environment))
        {
            List<JqValueSegment> segments = JqPaths.ParsePathValue(paths, context);
            JsonNode? value = JqPathReads.GetPath(pair.Value, segments);
            if (!pair.Tracked)
            {
                yield return new JqValuePath(pair.Segments, value, false);
                continue;
            }
            var extended = new List<JqValueSegment>(pair.Segments);
            extended.AddRange(segments);
            yield return new JqValuePath(extended, value, true);
        }
    }
}

// `setpath(PATHS; VALUES)`: cartesian value-outer combinations.
internal sealed class SetpathBuiltinFilter(JqFilter Paths, JqFilter Values) : JqFilter
{
    // Updates rebuild the root, so results travel untracked.
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? value in Values.Evaluate(input, context, environment))
            foreach (JsonNode? paths in Paths.Evaluate(input, context, environment))
                yield return JqPathUpdates.SetPath(input, JqPaths.ParsePathValue(paths, context), context.Runtime.Clone(value), context);
    }
}

// `delpaths(PATHS)`: deletes a value holding an array of paths.
internal sealed class DelpathsBuiltinFilter(JqFilter Paths) : JqFilter
{
    // Deletion rebuilds the root, so results travel untracked.
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? paths in Paths.Evaluate(input, context, environment))
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
    // Pick rebuilds from null, so its output is never identical to the input.
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JsonNode? state = null;
        foreach (JqFilter paths in Args)
            foreach (IReadOnlyList<JqValueSegment> segments in JqPaths.CollectPaths(paths, input, context, environment))
                state = JqPathUpdates.SetPath(state, segments, JqPathReads.GetPath(input, segments), context);
        yield return state;
    }
}

// Assignment and update operators. Paths enumerate once from the original
// input; right-hand values stream per combination with values outermost.
// `|=` threads evolving state through first-only updates and deletes paths
// whose update yields nothing.
internal sealed class AssignFilter(string Op, JqFilter Paths, JqFilter Values) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        List<IReadOnlyList<JqValueSegment>> paths = JqPaths.CollectPaths(Paths, input, context, environment);
        if (Op == "|=")
        {
            yield return ModifyLoop(paths, Values, input, context, environment);
            yield break;
        }
        foreach (JsonNode? value in Values.Evaluate(input, context, environment))
        {
            if (Op == "=")
            {
                JsonNode? assigned = input;
                foreach (IReadOnlyList<JqValueSegment> segments in paths)
                    assigned = JqPathUpdates.SetPath(assigned, segments, context.Runtime.Clone(value), context);
                yield return assigned;
            }
            else
            {
                yield return ModifyLoop(paths, UpdateFor(value, context), input, context, environment);
            }
        }
    }

    private JqFilter UpdateFor(JsonNode? value, JqContext context)
    {
        var literal = new LiteralFilter(context.Runtime.Clone(value));
        if (Op == "//=")
            return new AlternativeFilter(new IdentityFilter(), literal);
        string symbol = Op switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "/=" => "/",
            "%=" => "%",
            _ => throw new JqException($"unsupported assignment {Op}"),
        };
        return new BinaryFilter(new IdentityFilter(), symbol, literal);
    }

    private static JsonNode? ModifyLoop(IReadOnlyList<IReadOnlyList<JqValueSegment>> paths, JqFilter update, JsonNode? input, JqContext context, JqEnvironment environment)
    {
        JsonNode? state = input;
        var deleted = new List<IReadOnlyList<JqValueSegment>>();
        foreach (IReadOnlyList<JqValueSegment> segments in paths)
        {
            bool ran = false;
            using IEnumerator<JsonNode?> results = update.Evaluate(JqPathReads.GetPath(state, segments), context, environment).GetEnumerator();
            while (results.MoveNext())
            {
                state = JqPathUpdates.SetPath(state, segments, context.Runtime.Clone(results.Current), context);
                ran = true;
                break;
            }
            if (!ran)
                deleted.Add(segments);
        }
        return JqPathDeletes.DeletePaths(state, deleted, context);
    }
}
