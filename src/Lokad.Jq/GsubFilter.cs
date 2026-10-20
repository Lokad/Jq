using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
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
            using var ownedSubject = context.Regexes.EncodeSubject(text);
            ReadOnlyMemory<byte> subject = ownedSubject.Value;
            var matchOptions = options.Match;
            // jq aligns replacement streams by their result index across successive matches.
            context.Budget.ReserveMetadata(32, 1);
            var results = new List<StringBuilder>();
            var previous = 0;
            var start = 0;
            while (start <= subject.Length)
            {
                using var ownedMatch = context.Regexes.Match(regex, subject, start, matchOptions);
                var match = ownedMatch.Value;
                if (!match.Success) break;
                context.Budget.CheckCancellation();
                var end = match.Value.EndOffsetInUtf16;
                var endByte = match.Value.EndOffsetInBytes;
                var isEmpty = endByte == match.Value.StartOffsetInBytes;
                if (match.Value.StartOffsetInUtf16 < previous || end < match.Value.StartOffsetInUtf16)
                    throw new JqException("gsub requires non-overlapping forward matches");
                var gapStart = previous;
                var gapLength = match.Value.StartOffsetInUtf16 - previous;
                JqStorage? storage = context.Budget.TemporaryStorage;
                long captureBefore = storage?.UsedBytes ?? 0;
                JsonObject captures = JqMatch.FoldNamedCaptures(context, text, match, JqMatch.GroupNameMap(regex), match.CaptureSlotCount - 1);
                long captureDemand = storage is null ? 0 : storage.UsedBytes - captureBefore;

                // Positions and capture strings have been copied. Release the
                // provider result before evaluating nested replacements.
                ownedMatch.Dispose();
                var index = 0;
                long bodyBefore = storage?.UsedBytes ?? 0;
                long retainedBuilderDemand = 0;
                foreach (var replacement in args[1].Evaluate(captures, context, environment))
                {
                    long builderBefore = storage?.UsedBytes ?? 0;
                    if (index == results.Count)
                    {
                        context.Budget.CheckCancellation();
                        if (results.Count == results.Capacity)
                        {
                            int previousCapacity = results.Capacity;
                            int capacity = JqBudget.BufferCapacity((long)results.Count + 1, Math.Max(4, 2L * previousCapacity));
                            context.Budget.ReserveBytes(8L * capacity);
                            results.EnsureCapacity(capacity);
                            storage?.DiscardBytes(8L * previousCapacity);
                        }
                        context.Budget.ReserveMetadata(128, 2);
                        results.Add(new StringBuilder());
                    }
                    var result = results[index++];
                    context.Budget.Append(result, text.AsSpan(gapStart, gapLength));
                    // jq's string concatenation treats null as the identity.
                    if (replacement != null) context.Budget.Append(result, String(replacement));
                    if (storage is not null) retainedBuilderDemand += storage.UsedBytes - builderBefore;
                }
                // Replacement iterators have finished. Only copied builder data
                // escapes this match; captures and other body temporaries do not.
                // Failure bypasses this point so escaping payloads stay charged.
                if (storage is not null)
                    storage.DiscardBytes(captureDemand + storage.UsedBytes - bodyBefore - retainedBuilderDemand);
                previous = end;
                start = endByte;
                if (isEmpty)
                {
                    if (end == text.Length) break;
                    int width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length
                                && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                    start += Encoding.UTF8.GetByteCount(text.AsSpan(end, width));
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
