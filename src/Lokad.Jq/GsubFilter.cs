using System;
using System.Collections.Generic;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

internal sealed class GsubFilter(IReadOnlyList<JqFilter> args, bool firstOnly) : JqFilter
{

    protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
    {
        await foreach (var flags in (args.Count == 3 ? args[2].Evaluate(input, context, environment) : Enumerable.Repeat<JqValue?>(null, 1).ToAsyncEnumerable()).ConfigureAwait(false))
        await foreach (var pattern in args[0].Evaluate(input, context, environment).ConfigureAwait(false))
        {
            JqString text = JqMatch.RequireText(context, input);
            JqString? flagText = JqMatch.RequireModifiers(context, flags);
            JqRegexOptions options = JqRegexOptions.ParseOrThrow(flagText,
                firstOnly ? JqRegexGlobalFlag.None : JqRegexGlobalFlag.Suffix, context.Budget);
            // sub without an explicit g flag stops after the first match.
            bool stopFirst = !options.Global;
            using var patternLease = context.Regexes.Rent(JqMatch.RequirePattern(context, pattern).Utf8, options.Pattern);
            var regex = patternLease.Value;
            int captureCount = JqRegexCache.GetCaptureCount(regex);
            using var ownedSubject = context.Regexes.Prepare(regex, text.Utf8, null);
            ReadOnlyMemory<byte> subject = ownedSubject.Bytes;
            using var cursor = ownedSubject.CreateCursor(options.Match, context.Budget.CancellationToken);
            using var coordinates = new JqRegexCoordinates(subject, context.Budget);
            // jq aligns replacement streams by their result index across successive matches.
            context.Budget.ReserveMetadata(32, 1);
            var results = new List<JqMemoryValue<JqJsonBuffer>>();
            var previous = 0;
            try
            {
                while (cursor.MoveNext(context.Budget.CancellationToken))
                {
                    int end, gapLength;
                    var gapStart = previous;
                    JqStorage? storage = context.Budget.TemporaryStorage;
                    JqObject captures;
                    long captureDemand;
                    {
                        var match = cursor.Current;
                        context.Budget.CheckCancellation();
                        end = coordinates.At(match.Value.EndOffsetInBytes).Byte;
                        int matchStart = coordinates.At(match.Value.StartOffsetInBytes).Byte;
                        if (matchStart < previous || end < matchStart)
                            throw new JqException("gsub requires non-overlapping forward matches");
                        gapLength = matchStart - previous;
                        long captureBefore = storage?.UsedBytes ?? 0;
                        captures = JqMatch.FoldNamedCaptures(context, text, match, JqRegexCache.GetGroupNames(regex), captureCount, coordinates);
                        captureDemand = storage is null ? 0 : storage.UsedBytes - captureBefore;
                    }

                    // Capture data is copied before suspension. Do not read the
                    // borrowed current view during nested replacement evaluation.
                    var index = 0;
                    long bodyBefore = storage?.UsedBytes ?? 0;
                    long retainedBuilderDemand = 0;
                    await foreach (var replacement in args[1].Evaluate(captures, context, environment).ConfigureAwait(false))
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
                            results.Add(context.Budget.RentUtf8Builder());
                        }
                        JqJsonBuffer result = results[index++].Value;
                        Append(result, subject.Slice(gapStart, gapLength));
                        // jq's string concatenation treats null as the identity.
                        if (replacement is JqString replacementText) Append(result, replacementText.Utf8);
                        else if (replacement is not null) throw new JqException($"expected string, got {TypeName(replacement)}");
                        if (storage is not null) retainedBuilderDemand += storage.UsedBytes - builderBefore;
                    }
                    // Replacement iterators have finished. Only copied builder data
                    // escapes this match; captures and other body temporaries do not.
                    // Failure bypasses this point so escaping payloads stay charged.
                    if (storage is not null)
                        storage.DiscardBytes(captureDemand + storage.UsedBytes - bodyBefore - retainedBuilderDemand);
                    previous = end;
                    if (stopFirst) break;
                }

                if (results.Count == 0)
                {
                    yield return text.CloneHeader(context.Budget);
                    continue;
                }
                foreach (var result in results)
                {
                    Append(result.Value, subject.Slice(previous));
                    result.Value.ReleaseReplacedStorage();
                    JqString value = JqString.FromUtf8(result.Value.WrittenMemory.Span, context.Budget);
                    result.Dispose();
                    yield return value;
                }
            }
            finally
            {
                foreach (var result in results) result.Dispose();
            }
        }

        static void Append(JqJsonBuffer result, ReadOnlyMemory<byte> bytes)
        {
            JqBudget.BufferCapacity((long)result.WrittenMemory.Length + bytes.Length, 0);
            if (bytes.IsEmpty) return;
            bytes.Span.CopyTo(result.GetSpan(bytes.Length));
            result.Advance(bytes.Length);
        }
    }
}
