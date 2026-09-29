using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// `reduce SOURCE as PATTERNS (INIT; UPDATE)`: folds source items into
// accumulator states. Initial values stream outermost; per item, per state,
// the update runs in the matched environment (first-match-wins with
// catchable retries, mirroring bindings). Multi-valued updates branch the
// accumulator; empty updates kill their branch. Final states yield once.
internal sealed class ReduceFilter(JqFilter Source, IReadOnlyList<BindingPattern> Alternatives, JqFilter Init, JqFilter Update) : JqFilter
{
    private readonly HashSet<string> _allNames = AsFilter.CollectAll(Alternatives);

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? initial in Init.Evaluate(input, context, environment))
        {
            var states = new List<JsonNode?> { initial };
            foreach (JsonNode? item in Source.Evaluate(input, context, environment))
            {
                var next = new List<JsonNode?>();
                foreach (JsonNode? state in states)
                    foreach (var (_, updated) in AsFilter.DriveAlternatives(Alternatives, _allNames, item, environment, context, scope => Update.Evaluate(state, context, scope)))
                    {
                        context.Budget.ChargeNode();
                        next.Add(updated);
                    }
                states = next;
            }
            foreach (JsonNode? state in states)
                yield return state;
        }
    }
}

// `foreach SOURCE as PATTERNS (INIT; UPDATE[; EXTRACT])`: like reduce, but
// yields every intermediate state (or the extraction run against each
// updated state in the matched environment). Initial values never yield
// without items; empty updates end their branch silently.
internal sealed class ForeachFilter(JqFilter Source, IReadOnlyList<BindingPattern> Alternatives, JqFilter Init, JqFilter Update, JqFilter? Extract) : JqFilter
{
    private readonly HashSet<string> _allNames = AsFilter.CollectAll(Alternatives);

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? initial in Init.Evaluate(input, context, environment))
        {
            var states = new List<JsonNode?> { initial };
            foreach (JsonNode? item in Source.Evaluate(input, context, environment))
            {
                var next = new List<JsonNode?>();
                foreach (JsonNode? state in states)
                    foreach (var (scope, updated) in AsFilter.DriveAlternatives(Alternatives, _allNames, item, environment, context, scope => Update.Evaluate(state, context, scope)))
                    {
                        context.Budget.ChargeNode();
                        next.Add(updated);
                        if (Extract is null)
                            yield return updated;
                        else
                            foreach (JsonNode? extracted in Extract.Evaluate(updated, context, scope))
                                yield return extracted;
                    }
                states = next;
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

// `nth($n; EXPR)`: drops n outputs, then yields the next once.
internal sealed class NthFilter(JqFilter Index, JqFilter Body) : JqFilter
{
    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        foreach (JsonNode? index in Index.Evaluate(input, context, environment))
        {
            double position = Number(index);
            if (position < 0)
                throw new JqException("nth doesn't support negative indices");
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
            if (!AnyTruthy(Condition, state, context, environment))
                continue;
            yield return context.Runtime.Clone(state);
            var next = new List<JsonNode?>();
            foreach (JsonNode? updated in Update.Evaluate(state, context, environment))
            {
                context.Budget.ChargeNode();
                next.Add(updated);
            }
            for (int index = next.Count - 1; index >= 0; index--)
                pending.Push(next[index]);
        }
    }

    internal static bool AnyTruthy(JqFilter condition, JsonNode? state, JqContext context, JqEnvironment environment)
    {
        foreach (JsonNode? probe in condition.Evaluate(state, context, environment))
            if (Truthy(probe))
                return true;
        return false;
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
            if (WhileFilter.AnyTruthy(Condition, state, context, environment))
            {
                yield return context.Runtime.Clone(state);
                continue;
            }
            var next = new List<JsonNode?>();
            foreach (JsonNode? updated in Next.Evaluate(state, context, environment))
            {
                context.Budget.ChargeNode();
                next.Add(updated);
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

// `walk(filter)`: bottom-up traversal. Children rebuild first (taking the
// first walk output per child, dropping empties, like update assignment),
// then the filter runs against each rebuilt node.
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
        JsonNode? rebuilt = node;
        if (node is JsonArray array)
        {
            var walked = new JsonArray();
            context.Budget.ChargeNode();
            foreach (JsonNode? child in array)
            {
                using IEnumerator<JsonNode?> outputs = Walk(child, context, environment).GetEnumerator();
                if (outputs.MoveNext())
                    walked.Add(outputs.Current);
            }
            rebuilt = walked;
        }
        else if (node is JsonObject obj)
        {
            var walked = new JsonObject();
            context.Budget.ChargeNode();
            foreach (var property in obj)
            {
                using IEnumerator<JsonNode?> outputs = Walk(property.Value, context, environment).GetEnumerator();
                if (outputs.MoveNext())
                    walked.Add(property.Key, outputs.Current);
            }
            rebuilt = walked;
        }
        foreach (JsonNode? value in Body.Evaluate(rebuilt, context, environment))
            yield return value;
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
            foreach (JsonNode? probe in Condition.Evaluate(pair.Value, context, environment))
            {
                if (Truthy(probe))
                    yield return JqPaths.PathToJson(pair.Segments);
                break;
            }
        }
    }
}
