using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using PCRE;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

internal sealed class GsubFilter(IReadOnlyList<JqFilter> args, bool firstOnly) : JqFilter
{
    // Substitutions build fresh strings, so results travel untracked.
    internal override bool PreservesPathIdentity => false;

    protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
    {
        string filterName = firstOnly ? "sub" : "gsub";
        if (args.Count is not (2 or 3))
            throw new JqException(filterName + " expects two or three arguments");
        string text = JqMatch.RequireText(context, input);
        foreach (var flags in args.Count == 3 ? args[2].Evaluate(input, context, environment) : [(JsonNode?)null])
        foreach (var pattern in args[0].Evaluate(input, context, environment))
        {
            string flagText = JqMatch.RequireModifiers(context, flags);
            JqRegexOptions options = JqRegexOptions.ParseOrThrow(flagText);
            // sub without an explicit g flag stops after the first match.
            bool stopFirst = firstOnly && !flagText.Contains('g');
            var regex = context.Regexes.Get(JqMatch.RequirePattern(context, pattern), options.Pattern);
            var matchOptions = options.Match;
            // jq aligns replacement streams by their result index across successive matches.
            var results = new List<StringBuilder>();
            var previous = 0;
            var start = 0;
            while (start <= text.Length)
            {
                var match = context.Regexes.Match(regex, text, start, matchOptions);
                if (!match.Success) break;
                // The first search validates the whole immutable string. Rechecking every suffix
                // would make dense replacements quadratic; \C is forbidden so offsets stay scalar-aligned.
                matchOptions |= PcreMatchOptions.NoUtfCheck;
                context.Budget.ChargeNode();
                var end = match.EndIndex;
                var isEmpty = match.Length == 0;
                if (match.Index < previous || end < match.Index)
                    throw new JqException("gsub requires non-overlapping forward matches");
                var gapStart = previous;
                var gapLength = match.Index - previous;
                var captures = new JsonObject();
                context.Budget.ChargeNode();
                foreach (var name in regex.Regex.PatternInfo.GroupNames)
                {
                    context.Budget.ChargeNode();
                    context.Budget.ChargeString(name.Length);
                    var group = match[name];
                    if (group.Success) context.Budget.ChargeString(group.Length);
                    captures[name] = group.Success ? JsonValue.Create(group.Value.ToString()) : null;
                }

                // All match positions and captures are now copied. A nested gsub may reuse the buffer.
                var index = 0;
                foreach (var replacement in args[1].Evaluate(captures, context, environment))
                {
                    if (index == results.Count)
                    {
                        context.Budget.ChargeNode();
                        results.Add(new StringBuilder());
                    }
                    var result = results[index++];
                    context.Budget.Append(result, text.AsSpan(gapStart, gapLength));
                    // jq's string concatenation treats null as the identity.
                    if (replacement != null) context.Budget.Append(result, String(replacement));
                }
                previous = end;
                start = end;
                if (isEmpty)
                {
                    if (start == text.Length) break;
                    start += char.IsHighSurrogate(text[start]) && start + 1 < text.Length
                             && char.IsLowSurrogate(text[start + 1]) ? 2 : 1;
                }
                if (stopFirst) break;
            }

            if (results.Count == 0)
            {
                yield return JsonValue.Create(text);
                continue;
            }
            foreach (var result in results)
            {
                context.Budget.Append(result, text.AsSpan(previous));
                yield return JsonValue.Create(context.Budget.Finish(result));
            }
        }
    }
}
