using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// `tostream`: emits the streamed form of the input: `[path, leaf]` for
// scalars and empty containers plus `[path]` closes for completed
// containers, in the same order as `--stream` decoding. Close paths keep
// the last child segment, so even the root container closes when non-empty.
internal sealed class TostreamFilter : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        using var cursor = new JqTreeCursor(context.Budget);
        JsonNode? current = input;
        while (true)
        {
            context.Budget.CheckCancellation();
            if (current is JsonArray { Count: > 0 } or JsonObject { Count: > 0 })
            {
                cursor.Push(current);
                if (!cursor.TryNext(out current)) throw new InvalidOperationException("A nonempty container changed during traversal.");
                continue;
            }
            JsonNode? leaf = context.Runtime.Clone(current);
            context.Budget.ReserveBytes(112);
            yield return new JsonArray { cursor.CopyJsonPath(), leaf };
            while (cursor.Count != 0)
            {
                if (cursor.TryNext(out current)) break;
                context.Budget.ReserveBytes(88);
                yield return new JsonArray { cursor.CopyJsonPath() };
                cursor.Pop();
            }
            if (cursor.Count == 0) yield break;
        }
    }
}

// `fromstream(STREAM)`: rebuilds values from `[path, leaf]` and `[path]`
// events with the reference fold: length-two events set the leaf (emitting
// immediately for empty paths) while any other event length emits the
// accumulated value when its path holds exactly one element.
internal sealed class FromstreamFilter(JqFilter Stream) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JsonNode? state = null;
        bool emit = false;
        await foreach (JsonNode? item in Stream.Evaluate(input, context, environment).ConfigureAwait(false))
        {
            if (emit)
            {
                state = null;
                emit = false;
            }
            if (item is not JsonArray pair)
                throw new JqException("fromstream input must be an array");
            if (pair.Count == 2)
            {
                List<JqValueSegment> segments = EventPath(pair[0], context);
                emit = segments.Count == 0;
                state = JqPathUpdates.SetOwnedPath(state, segments, pair[1], context);
            }
            else
            {
                emit = pair.Count > 0 && EventPathLength(pair[0]) == 1;
            }
            if (emit)
                yield return state;
        }
    }

    private static List<JqValueSegment> EventPath(JsonNode? path, JqContext context)
    {
        if (path is not JsonArray array)
            throw new JqException("fromstream path must be an array");
        return JqPaths.ParsePathValue(array, context);
    }

    private static int EventPathLength(JsonNode? path) =>
        path is JsonArray array ? array.Count : throw new JqException("fromstream path must be an array");
}

// `truncate_stream(STREAM)`: consumes a depth as input and drops that many
// leading path elements from each event of the argument stream evaluated
// against null; events left with no deeper path are omitted.
internal sealed class TruncateStreamFilter(JqFilter Stream) : JqFilter
{
    protected override async IAsyncEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        IReadOnlyList<JqValueSegment> first = [new IndexSegment(0, false)];
        IReadOnlyList<JqValueSegment>? slice = null;
        await foreach (JsonNode? item in Stream.Evaluate(null, context, environment).ConfigureAwait(false))
        {
            JsonNode? path = JqPathReads.GetPath(item, first, context);
            if (context.Runtime.Compare(context.Runtime.Length(path), input) <= 0)
                continue;
            if (slice is null)
            {
                double? depth = null;
                if (input is not null)
                {
                    if (!FlattenFilter.TryDepthLevel(input, out double number))
                        throw new JqException("Array/string slice indices must be integers");
                    if (!double.IsNaN(number)) depth = number;
                }
                slice = [new SliceSegment(depth, null)];
            }
            JsonNode? truncated = JqPathReads.GetPath(path, slice, context);
            int count = item is JsonArray pair ? pair.Count : 0;
            context.Budget.ReserveBytes(64 + 24L * Math.Max(1, count));
            var reshaped = new JsonArray { truncated };
            if (item is JsonArray source)
                for (int i = 1; i < source.Count; i++)
                {
                    context.Budget.ReserveArrayItem(source[i]);
                    reshaped.Add(context.Budget.CopyValue(source[i], 1));
                }
            yield return reshaped;
        }
    }
}
