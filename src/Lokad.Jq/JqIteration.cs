using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

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
