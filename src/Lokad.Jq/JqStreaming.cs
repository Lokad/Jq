using System;
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (input is not JsonArray array || array.Count == 0)
        {
            if (input is not JsonObject obj || obj.Count == 0)
            {
                context.Budget.ChargeNode();
                yield return new JsonArray { new JsonArray(), context.Runtime.Clone(input) };
                yield break;
            }
        }
        var segments = new List<JsonNode?>();
        var frames = new Stack<ContainerFrame>();
        JsonNode? node = input;
        while (true)
        {
            if (node is JsonArray items && items.Count > 0)
            {
                frames.Push(new ContainerFrame(items, new List<string>(), 0));
                segments.Add(JsonValue.Create(0L));
                node = items[0];
                continue;
            }
            if (node is JsonObject props && props.Count > 0)
            {
                var keys = new List<string>();
                foreach (var property in props)
                    keys.Add(property.Key);
                frames.Push(new ContainerFrame(props, keys, 0));
                segments.Add(JsonValue.Create(keys[0]));
                node = props[keys[0]];
                continue;
            }
            context.Budget.ChargeNode();
            yield return StreamLeaf(segments, context.Runtime.Clone(node));
            while (true)
            {
                if (frames.Count == 0)
                    yield break;
                ContainerFrame frame = frames.Peek();
                if (frame.Container is JsonArray parent)
                {
                    int next = frame.Position + 1;
                    if (next < parent.Count)
                    {
                        frames.Pop();
                        frames.Push(frame.Advance(next));
                        segments[segments.Count - 1] = JsonValue.Create((long)next);
                        node = parent[next];
                        break;
                    }
                    frames.Pop();
                    context.Budget.ChargeNode();
                    yield return StreamClose(segments);
                    segments.RemoveAt(segments.Count - 1);
                    node = parent;
                    continue;
                }
                else
                {
                    var objParent = (JsonObject)frame.Container;
                    if (frame.Position + 1 < frame.Keys.Count)
                    {
                        int next = frame.Position + 1;
                        string key = frame.Keys[next];
                        frames.Pop();
                        frames.Push(frame.Advance(next));
                        segments[segments.Count - 1] = JsonValue.Create(key);
                        node = objParent[key];
                        break;
                    }
                    frames.Pop();
                    context.Budget.ChargeNode();
                    yield return StreamClose(segments);
                    segments.RemoveAt(segments.Count - 1);
                    node = objParent;
                    continue;
                }
            }
        }
    }

    private static JsonArray StreamLeaf(List<JsonNode?> segments, JsonNode? leaf)
    {
        var path = new JsonArray();
        foreach (JsonNode? segment in segments)
            path.Add(segment?.DeepClone());
        return new JsonArray { path, leaf };
    }

    private static JsonArray StreamClose(List<JsonNode?> segments)
    {
        var path = new JsonArray();
        foreach (JsonNode? segment in segments)
            path.Add(segment?.DeepClone());
        return new JsonArray { path };
    }

    private sealed record ContainerFrame(JsonNode Container, List<string> Keys, int Position)
    {
        public ContainerFrame Advance(int position) => this with { Position = position };
    }
}

// `fromstream(STREAM)`: rebuilds values from `[path, leaf]` and `[path]`
// events with the reference fold: length-two events set the leaf (emitting
// immediately for empty paths) while any other event length emits the
// accumulated value when its path holds exactly one element.
internal sealed class FromstreamFilter(JqFilter Stream) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        JsonNode? state = null;
        bool emit = false;
        foreach (JsonNode? item in Stream.Evaluate(input, context, environment))
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
                state = JqPathUpdates.SetPath(state, segments, context.Runtime.Clone(pair[1]), context);
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
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        if (!JqRuntime.TryGetInt(input, out int depth))
            throw new JqException("truncate_stream requires a numeric depth");
        foreach (JsonNode? item in Stream.Evaluate(null, context, environment))
        {
            if (item is not JsonArray pair || pair.Count == 0)
                continue;
            int pathLength;
            JsonArray? path;
            if (pair[0] is null)
            {
                path = null;
                pathLength = 0;
            }
            else if (pair[0] is JsonArray array)
            {
                path = array;
                pathLength = array.Count;
            }
            else
            {
                throw new JqException("truncate_stream path must be an array");
            }
            if (depth >= 0 && pathLength <= depth)
                continue;
            int start = depth < 0 ? Math.Max(0, pathLength + depth) : Math.Min(depth, pathLength);
            var truncated = new JsonArray();
            for (int i = start; i < pathLength; i++)
                truncated.Add(path![i]?.DeepClone());
            context.Budget.ChargeNode();
            var reshaped = new JsonArray { truncated };
            for (int i = 1; i < pair.Count; i++)
                reshaped.Add(context.Runtime.Clone(pair[i]));
            yield return reshaped;
        }
    }
}

