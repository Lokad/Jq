using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// `reduce SOURCE as PATTERNS (INIT; UPDATE)`: folds source items into a
// single answer per initializer. Initial values stream outermost; per item,
// the update runs against the running state in the matched environment
// (first-match-wins with catchable retries, mirroring bindings). Like the
// reference cell overwrite (compile.c gen_reduce, gojq compileReduce), the
// last update output wins and earlier outputs are dropped; an update with no
// outputs empties the cell (reference LOADVN slot) and the run continues.
// Each initializer yields its final state once, even when no update
// ever produced a value.
internal sealed class ReduceFilter(JqFilter Source, IReadOnlyList<BindingPattern> Alternatives, JqFilter Init, JqFilter Update) : JqFilter
{
    private readonly HashSet<string> _allNames = AsFilter.CollectAll(Alternatives);

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? initial in Init.Evaluate(input, context, environment))
        {
            JsonNode? state = initial;
            foreach (JsonNode? item in Source.Evaluate(input, context, environment))
            {
                bool produced = false;
                JsonNode? last = null;
                foreach (var (_, updated) in AsFilter.DriveAlternatives(Alternatives, _allNames, item, environment, context, scope => Update.Evaluate(state, context, scope)))
                {
                    context.Budget.ChargeNode();
                    produced = true;
                    last = updated;
                }

                // Like the reference LOADVN slot, an update with no outputs
                // empties the cell and the run continues with the next item.
                state = produced ? last : null;
            }

            yield return state;
        }
    }
}

// `foreach SOURCE as PATTERNS (INIT; UPDATE[; EXTRACT])`: like reduce, but
// yields every intermediate state (or the extraction run against each
// updated state in the matched environment). Initial values never yield
// without items; each update output is emitted while only the last threads
// forward as state. An update with no outputs empties the cell like the
// reference LOADVN slot, skips extraction, and continues with the next item.
internal sealed class ForeachFilter(JqFilter Source, IReadOnlyList<BindingPattern> Alternatives, JqFilter Init, JqFilter Update, JqFilter? Extract) : JqFilter
{
    private readonly HashSet<string> _allNames = AsFilter.CollectAll(Alternatives);

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? initial in Init.Evaluate(input, context, environment))
        {
            JsonNode? state = initial;
            foreach (JsonNode? item in Source.Evaluate(input, context, environment))
            {
                bool produced = false;
                JsonNode? last = null;
                foreach (var (scope, updated) in AsFilter.DriveAlternatives(Alternatives, _allNames, item, environment, context, scope => Update.Evaluate(state, context, scope)))
                {
                    context.Budget.ChargeNode();
                    produced = true;
                    last = updated;
                    if (Extract is null)
                        yield return updated;
                    else
                        foreach (JsonNode? extracted in Extract.Evaluate(updated, context, scope))
                            yield return extracted;
                }

                // Like the reference LOADVN slot, an update with no outputs
                // empties the cell, skips extraction, and continues.
                state = produced ? last : null;
            }
        }
    }
}

// `limit($n; EXPR)`: at most ceil(n) outputs. Zero never evaluates the
// argument; negative counts raise a catchable error.
internal sealed class LimitFilter(JqFilter Count, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? count in Count.Evaluate(input, context, environment))
        {
            // Null and booleans rank below numbers, so the reference order branches reject them like negatives.
            if (count is null || (count is JsonValue countBool && countBool.TryGetValue<bool>(out _)))
                throw new JqException("limit doesn't support negative count");
            double total = Number(count);
            if (total == 0)
                continue;
            if (!(total > 0))
                throw new JqException("limit doesn't support negative count");
            long pulled = 0;
            foreach (JsonNode? value in Body.Evaluate(input, context, environment))
            {
                yield return value;
                if (++pulled >= total)
                    break;
            }
        }
    }
}

// `first(EXPR)`: the first output only, pulled lazily.
internal sealed class FirstFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? value in Body.Evaluate(input, context, environment))
        {
            yield return value;
            yield break;
        }
    }
}

// `last(EXPR)`: the last output only, empty when the argument yields nothing.
internal sealed class LastFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        bool empty = true;
        JsonNode? last = null;
        foreach (JsonNode? value in Body.Evaluate(input, context, environment))
        {
            empty = false;
            last = value;
        }
        if (!empty)
            yield return last;
    }
}

// `nth($n; EXPR)`: drops n outputs, then yields the next once.
internal sealed class NthFilter(JqFilter Index, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? index in Index.Evaluate(input, context, environment))
        {
            // Null and booleans rank below numbers, so the reference order branches reject them like negatives.
            if (index is null || (index is JsonValue indexBool && indexBool.TryGetValue<bool>(out _)))
                throw new JqException("nth doesn't support negative indices");
            double position = Number(index);
            if (position < 0)
                throw new JqException("nth doesn't support negative indices");
            // The desugared skip surfaces its own diagnostic for NaN like the
            // reference else branch (NaN is neither positive nor zero).
            if (double.IsNaN(position))
                throw new JqException("skip doesn't support negative count");
            long skip = (long)position;
            using IEnumerator<JsonNode?> results = Body.Evaluate(input, context, environment).GetEnumerator();
            bool exhausted = false;
            for (long taken = 0; taken < skip; taken++)
            {
                if (!results.MoveNext())
                {
                    exhausted = true;
                    break;
                }
            }
            if (!exhausted && results.MoveNext())
                yield return results.Current;
        }
    }
}

// `isempty(EXPR)`: true when the argument yields nothing. Pulls at most one
// output; argument errors propagate instead of reading as empty.
internal sealed class IsemptyFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        using IEnumerator<JsonNode?> results = Body.Evaluate(input, context, environment).GetEnumerator();
        yield return JsonValue.Create(!results.MoveNext());
    }
}

// `while(cond; update)`: yields each state while the condition holds,
// branching depth-first over multi-valued updates on an explicit stack.
internal sealed class WhileFilter(JqFilter Condition, JqFilter Update) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var pending = new Stack<JsonNode?>();
        pending.Push(input);
        while (pending.TryPop(out JsonNode? state))
        {
            // Reference distribution like if: every condition output counts,
            // so repeated truthy probes duplicate the state and its updates.
            var next = new List<JsonNode?>();
            foreach (JsonNode? probe in Condition.Evaluate(state, context, environment))
            {
                if (!Truthy(probe))
                    continue;
                yield return context.Runtime.Clone(state);
                foreach (JsonNode? updated in Update.Evaluate(state, context, environment))
                {
                    context.Budget.ChargeNode();
                    next.Add(updated);
                }
            }
            for (int index = next.Count - 1; index >= 0; index--)
                pending.Push(next[index]);
        }
    }
}

// `until(cond; next)`: yields the first state satisfying the condition per
// branch, depth-first over multi-valued updates.
internal sealed class UntilFilter(JqFilter Condition, JqFilter Next) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var pending = new Stack<JsonNode?>();
        pending.Push(input);
        while (pending.TryPop(out JsonNode? state))
        {
            // Reference distribution like if: truthy probes yield the state
            // while falsy probes recurse, each independently.
            var next = new List<JsonNode?>();
            foreach (JsonNode? probe in Condition.Evaluate(state, context, environment))
            {
                if (Truthy(probe))
                {
                    yield return context.Runtime.Clone(state);
                    continue;
                }
                foreach (JsonNode? updated in Next.Evaluate(state, context, environment))
                {
                    context.Budget.ChargeNode();
                    next.Add(updated);
                }
            }
            for (int index = next.Count - 1; index >= 0; index--)
                pending.Push(next[index]);
        }
    }
}

// `repeat(filter)`: re-evaluates the argument against the original input
// forever, yielding a constant stream per round. Long runs obey the value
// budget through per-round charges.
internal sealed class RepeatFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        while (true)
        {
            context.Budget.ChargeNode();
            foreach (JsonNode? value in Body.Evaluate(input, context, environment))
                yield return value;
        }
    }
}

// `recurse(filter[; condition])`: depth-first pre-order traversal, flat on
// an explicit stack. The condition filters expanded values like select.
internal sealed class RecurseFilter(JqFilter Body, JqFilter? Condition) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var pending = new Stack<JsonNode?>();
        pending.Push(input);
        while (pending.TryPop(out JsonNode? state))
        {
            yield return context.Runtime.Clone(state);
            var next = new List<JsonNode?>();
            foreach (JsonNode? expanded in Body.Evaluate(state, context, environment))
            {
                if (Condition is not null && !FirstTruthy(Condition, expanded, context, environment))
                    continue;
                context.Budget.ChargeNode();
                next.Add(expanded);
            }
            for (int index = next.Count - 1; index >= 0; index--)
                pending.Push(next[index]);
        }
    }

    private static bool FirstTruthy(JqFilter condition, JsonNode? state, JqContext context, JqEnvironment environment)
    {
        foreach (JsonNode? probe in condition.Evaluate(state, context, environment))
            return Truthy(probe);
        return false;
    }
}

// `walk(filter)`: bottom-up traversal. Array levels rebuild by collecting
// every child walk output (reference map), object levels keep the first
// output per value and drop empties (reference map_values modify
// first-only, like update assignment), and the filter then runs against
// each rebuilt node, streaming every output upward.
internal sealed class WalkFilter(JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? walked in Walk(input, context, environment))
            yield return walked;
    }

    private IEnumerable<JsonNode?> Walk(JsonNode? node, JqContext context, JqEnvironment environment)
    {
        // The root rebuilt node streams every body output; nested array levels
        // collect every child output while nested object levels contribute only
        // their first, matching the reference child reads below.
        JsonNode? rebuilt = BuildRebuilt(node, context, environment);
        foreach (JsonNode? value in Body.Evaluate(rebuilt, context, environment))
            yield return value;
    }

    // Heap-allocated frame for iterative bottom-up traversal. Walk depth is value
    // depth, which setpath-built trees push far past the ingress depth cap, so
    // nested enumerator frames are not an option.
    private sealed class WalkFrame(JsonNode? node, string? key, int index)
    {
        public JsonNode? Node = node;
        public string? Key = key;
        public int Index = index;
        public List<WalkChild> Children = new();
        public int NextChild;
        public List<Walklevel> Parts = new();
    }

    private sealed record WalkChild(string? Key, int Index, JsonNode? Node);

    private sealed record Walklevel(string? Key, int Index, List<JsonNode?> Outputs);

    private static void FillWalkChildren(WalkFrame frame)
    {
        if (frame.Node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
                frame.Children.Add(new WalkChild(null, index, array[index]));
            return;
        }
        if (frame.Node is JsonObject obj)
        {
            foreach (var property in obj)
                frame.Children.Add(new WalkChild(property.Key, -1, property.Value));
        }
    }

    private static JsonNode? RebuildWalkLevel(WalkFrame frame, JqContext context)
    {
        if (frame.Node is JsonArray)
        {
            context.Budget.ChargeNode();
            var walked = new JsonArray();
            foreach (Walklevel part in frame.Parts)
                foreach (JsonNode? output in part.Outputs)
                    walked.Add(context.Runtime.Detach(output));
            return walked;
        }
        if (frame.Node is JsonObject)
        {
            context.Budget.ChargeNode();
            var walked = new JsonObject();
            foreach (Walklevel part in frame.Parts)
                if (part.Key is string name && part.Outputs.Count > 0)
                    walked.Add(name, context.Runtime.Detach(part.Outputs[0]));
            return walked;
        }
        return frame.Node;
    }

    // First body output of a rebuilt level, or nothing when the body is empty.
    // Empty levels drop out of their parent rebuild like the reference.
    private JsonNode? FirstWalkOutput(JsonNode? rebuilt, JqContext context, JqEnvironment environment, out bool hasOutput)
    {
        using IEnumerator<JsonNode?> outputs = Body.Evaluate(rebuilt, context, environment).GetEnumerator();
        if (!outputs.MoveNext())
        {
            hasOutput = false;
            return null;
        }
        hasOutput = true;
        return outputs.Current;
    }

    // Every body output of a rebuilt level. Object levels use the first-only
    // read above; array levels collect the whole stream, so empty levels
    // contribute zero elements like the reference.
    private List<JsonNode?> CollectWalkOutputs(JsonNode? rebuilt, JqContext context, JqEnvironment environment)
    {
        var outputs = new List<JsonNode?>();
        foreach (JsonNode? value in Body.Evaluate(rebuilt, context, environment))
        {
            context.Budget.CheckCancellation();
            context.Budget.ChargeNode();
            outputs.Add(value);
        }
        return outputs;
    }

    private JsonNode? BuildRebuilt(JsonNode? node, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var stack = new Stack<WalkFrame>();
        var root = new WalkFrame(node, null, -1);
        FillWalkChildren(root);
        stack.Push(root);
        while (stack.Count > 0)
        {
            context.Budget.CheckCancellation();
            WalkFrame frame = stack.Peek();
            if (frame.NextChild < frame.Children.Count)
            {
                WalkChild child = frame.Children[frame.NextChild];
                frame.NextChild++;
                WalkFrame childFrame = new WalkFrame(child.Node, child.Key, child.Index);
                FillWalkChildren(childFrame);
                stack.Push(childFrame);
                continue;
            }
            JsonNode? rebuilt = RebuildWalkLevel(frame, context);
            stack.Pop();
            if (stack.Count == 0)
                return rebuilt;
            if (stack.Peek().Node is JsonArray)
            {
                List<JsonNode?> outputs = CollectWalkOutputs(rebuilt, context, environment);
                stack.Peek().Parts.Add(new Walklevel(frame.Key, frame.Index, outputs));
            }
            else
            {
                JsonNode? first = FirstWalkOutput(rebuilt, context, environment, out bool hasOutput);
                if (hasOutput)
                    stack.Peek().Parts.Add(new Walklevel(frame.Key, frame.Index, new List<JsonNode?> { first }));
            }
        }
        throw new InvalidOperationException("Walk left no rebuilt node.");
    }
}

// `paths` and `paths(filter)`: non-empty descent paths, optionally keeping
// only nodes that satisfy the filter (first-truthy, mirroring select).
internal sealed class PathsFilter(JqFilter? Condition) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        var root = new JqValuePath(new List<JqValueSegment>(), input, true);
        foreach (JqValuePath pair in new RecursiveDescentFilter().EvaluatePaths(root, context, environment))
        {
            if (pair.Segments.Count == 0)
                continue;
            if (Condition is null)
            {
                yield return JqPaths.PathToJson(pair.Segments);
                continue;
            }
            // Like select, every condition output counts: each truthy probe keeps
            // the path, matching path(recurse|select(node_filter)) multiplicity.
            foreach (JsonNode? probe in Condition.Evaluate(pair.Value, context, environment))
            {
                if (Truthy(probe))
                    yield return JqPaths.PathToJson(pair.Segments);
            }
        }
    }
}

// `skip($n; EXPR)`: drops n outputs, then streams the rest.
internal sealed class SkipFilter(JqFilter Count, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? count in Count.Evaluate(input, context, environment))
        {
            // Null and booleans rank below numbers, so the reference order branches reject them like negatives.
            if (count is null || (count is JsonValue skipBool && skipBool.TryGetValue<bool>(out _)))
                throw new JqException("skip doesn't support negative count");
            double total = Number(count);
            // NaN falls through to the reference else branch like negatives.
            if (total < 0 || double.IsNaN(total))
                throw new JqException("skip doesn't support negative count");
            long skip = (long)total;
            using IEnumerator<JsonNode?> results = Body.Evaluate(input, context, environment).GetEnumerator();
            for (long dropped = 0; dropped < skip; dropped++)
            {
                if (!results.MoveNext())
                    break;
            }
            while (results.MoveNext())
                yield return results.Current;
        }
    }
}
