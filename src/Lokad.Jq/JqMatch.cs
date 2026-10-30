using System;
using System.Collections.Generic;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// Shared upstream-shaped regex search behind match, capture, scan, splits,
// and sub. Offsets and lengths count Unicode scalars, and global iteration
// advances by scalar after empty matches. Matches are collected eagerly
// like the reference, which builds one array per call.
internal static class JqMatch
{
    internal static bool Test(JqContext context, JqValue? input, JqValue? patternNode, JqValue? flagsNode)
    {
        JqString text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        JqRegexOptions options = JqRegexOptions.ParseOrThrow(RequireModifiers(context, flagsNode));
        using var patternLease = context.Regexes.Rent(pattern, options.Pattern);
        var regex = patternLease.Value;
        return context.Regexes.IsMatch(regex, text.Utf8, options.Match);
    }

    internal static List<JqValue> Search(JqContext context, JqValue? input, JqValue? patternNode, JqValue? modifiersNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        JqString text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        return SearchText(context, text, pattern, RequireModifiers(context, modifiersNode), Projection.Match);
    }

    internal static string RequirePattern(JqContext context, JqValue? patternNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (patternNode is not JqString pattern)
            throw new JqException(TypeName(patternNode) + " (" + context.Runtime.TruncatedDump(patternNode) + ") is not a string");
        return pattern.ToHost(context.Budget);
    }

    internal static JqString RequireText(JqContext context, JqValue? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (input is not JqString text)
            throw new JqException(TypeName(input) + " (" + context.Runtime.TruncatedDump(input) + ") cannot be matched, as it is not a string");
        return text;
    }

    internal static string RequireModifiers(JqContext context, JqValue? modifiersNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (modifiersNode is null)
            return string.Empty;
        if (modifiersNode is JqString flags)
            return flags.ToHost(context.Budget);
        throw new JqException(TypeName(modifiersNode) + " (" + context.Runtime.TruncatedDump(modifiersNode) + ") is not a string");
    }

    private enum Projection { Match, Scan, Split }

    private static List<JqValue> SearchText(JqContext context, JqString text, string pattern, string modifiers, Projection projection)
    {
        JqRegexOptions options = JqRegexOptions.ParseOrThrow(modifiers);
        bool global = modifiers.Contains('g');
        using var patternLease = context.Regexes.Rent(pattern, options.Pattern);
        var compiled = patternLease.Value;
        var names = projection == Projection.Match ? JqRegexCache.GetGroupNames(compiled) : null;
        ReadOnlyMemory<byte> subject = text.Utf8;
        context.Budget.ReserveMetadata(32, 1);
        var results = new List<JqValue>();
        using var coordinates = new JqRegexCoordinates(subject, context.Budget);
        int previousSplitEnd = 0;
        int start = 0;
        while (true)
        {
            using var match = context.Regexes.Match(compiled, subject, start, options.Match);
            if (!match.Success) break;
            context.Budget.CheckCancellation();
            int byteStart = coordinates.At(match.Value.StartOffsetInBytes).Byte;
            int byteEnd = coordinates.At(match.Value.EndOffsetInBytes).Byte;
            switch (projection)
            {
                case Projection.Match:
                    AddResult(BuildMatch(context, text, match,
                        names ?? throw new InvalidOperationException("Expected regex group metadata."),
                        match.CaptureSlotCount - 1, coordinates));
                    break;
                case Projection.Scan:
                    AddResult(ScanMatch(match));
                    break;
                case Projection.Split:
                    AddResult(Slice(previousSplitEnd, byteStart));
                    previousSplitEnd = byteEnd;
                    break;
            }
            if (!global) break;
            if (byteEnd == byteStart)
            {
                if (byteEnd == text.ByteLength) break;
                start = coordinates.AfterEmpty(match.Value.EndOffsetInBytes);
            }
            else start = match.Value.EndOffsetInBytes;
            if (start > subject.Length) break;
        }
        if (projection == Projection.Split) AddResult(Slice(previousSplitEnd, text.ByteLength));
        return results;

        void AddResult(JqValue value)
        {
            if (results.Count == results.Capacity)
            {
                int previous = results.Capacity;
                int capacity = JqBudget.BufferCapacity((long)results.Count + 1, Math.Max(4, 2L * previous));
                context.Budget.ReserveBytes(8L * capacity);
                results.EnsureCapacity(capacity);
                context.Budget.TemporaryStorage?.DiscardBytes(8L * previous);
            }
            results.Add(value);
        }

        JqValue ScanMatch(JqRegexMatch match)
        {
            int captures = match.CaptureSlotCount - 1;
            if (captures == 0) return Slice(coordinates.At(match.Value.StartOffsetInBytes).Byte, coordinates.At(match.Value.EndOffsetInBytes).Byte);
            context.Budget.ReserveMetadata(64 + 24L * captures, (int)Math.Min(32, 1L + captures));
            var row = new JqArray();
            for (int index = 1; index <= captures; index++)
            {
                context.Budget.CheckCancellation();
                if (!match.TryGetGroup(index, out JqRegexCapture group)) break;
                row.Add(group.Success ? CopyString(coordinates.At(group.StartOffsetInBytes).Byte, coordinates.At(group.EndOffsetInBytes).Byte) : null);
            }
            return row;
        }

        JqValue Slice(int from, int to)
        {
            return CopyString(from, to);
        }

        JqString CopyString(int from, int to) => text.SliceBytes(from, to, context.Budget);

    }

    // Named-capture fold for sub/gsub replacements: numbered groups in
    // order with last-wins, including null for non-participating groups,
    // mirroring `def sub`'s reduce over .captures in builtin.jq. Duplicate
    // names (enabled via PCRE2_DUPNAMES) fold the same way.
    internal static JqObject FoldNamedCaptures(JqContext context, JqString text, JqRegexMatch match, IReadOnlyDictionary<int, string> names, int captureCount, JqRegexCoordinates coordinates)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(names);
        context.Budget.ReserveMetadata(64 + 224L * names.Count, (int)Math.Min(32, 1L + 2L * names.Count));
        var captures = new JqObject(context.Budget);
        context.Budget.CheckCancellation();
        for (int index = 1; index <= captureCount; index++)
        {
            if (!names.TryGetValue(index, out string? name) || name is null)
                continue;
            if (!match.TryGetGroup(index, out JqRegexCapture group))
                break;
            context.Budget.CheckCancellation();
            if (group.Success)
            {
                var from = coordinates.At(group.StartOffsetInBytes);
                var to = coordinates.At(group.EndOffsetInBytes);
                captures[name] = text.SliceBytes(from.Byte, to.Byte, context.Budget);
            }
            else
            {
                captures[name] = null;
            }
        }
        return captures;
    }

    private static JqObject BuildMatch(JqContext context, JqString text, JqRegexMatch match, IReadOnlyDictionary<int, string> names, int captureCount, JqRegexCoordinates coordinates)
    {
        // Retained DOM headers, dictionaries, scalar wrappers and capture slots;
        // payload copies are admitted separately below. Keep this construction
        // estimate with the record instead of recounting the completed tree.
        context.Budget.ReserveMetadata(704 + 728L * captureCount, (int)Math.Min(32, 5L + 5L * captureCount));
        var matchStart = coordinates.At(match.Value.StartOffsetInBytes);
        var matchEnd = coordinates.At(match.Value.EndOffsetInBytes);
        JqString whole = text.SliceBytes(matchStart.Byte, matchEnd.Byte, context.Budget);
        int offset = matchStart.Scalar;
        int end = matchEnd.Scalar;
        var result = new JqObject(context.Budget) {
            ["offset"] = JqScalar.Create(offset),
            ["length"] = JqScalar.Create(end - offset),
            ["string"] = whole,
        };
        var captures = new JqArray();
        for (int index = 1; index <= captureCount; index++)
        {
            if (!match.TryGetGroup(index, out JqRegexCapture group))
                break;
            context.Budget.CheckCancellation();
            var capture = new JqObject(context.Budget);
            if (!group.Success)
            {
                capture["offset"] = JqScalar.Create(-1);
                capture["length"] = JqScalar.Create(0);
                capture["string"] = null;
            }
            else
            {
                var from = coordinates.At(group.StartOffsetInBytes);
                var to = coordinates.At(group.EndOffsetInBytes);
                JqString value = text.SliceBytes(from.Byte, to.Byte, context.Budget);
                capture["offset"] = JqScalar.Create(from.Scalar);
                capture["length"] = JqScalar.Create(to.Scalar - from.Scalar);
                capture["string"] = value;
            }
            if (names.TryGetValue(index, out string? name))
            {
                capture["name"] = JqScalar.Create(name, context.Budget);
            }
            else
            {
                capture["name"] = null;
            }
            captures.Add(capture);
        }
        result["captures"] = captures;
        return result;
    }

    internal static JqObject ToNamedObject(JqContext context, JqObject match)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(match);
        context.Budget.ReserveMetadata(64, 1);
        var result = new JqObject(context.Budget);
        if (match["captures"] is JqArray captures)
        {
            foreach (JqValue? item in captures)
            {
                if (item is JqObject capture
                    && capture["name"] is JqString name)
                {
                    context.Budget.CheckCancellation();
                    context.Budget.ReserveMetadata(224, 2);
                    result[name] = context.Runtime.Clone(capture["string"]);
                }
            }
        }
        return result;
    }

    internal static void SplitArgument(JqContext context, JqValue? value, out JqValue? pattern, out JqValue? modifiers)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (value is JqArray array)
        {
            if (array.Count > 1)
            {
                pattern = array[0];
                modifiers = array[1];
                return;
            }
            if (array.Count > 0)
            {
                pattern = array[0];
                modifiers = null;
                return;
            }
        }
        else if (value is JqString)
        {
            pattern = value;
            modifiers = null;
            return;
        }
        string message = TypeName(value) + " not a string or array";
        context.Budget.ReserveString(message.Length);
        throw new JqErrorException(JqScalar.Create(message, context.Budget));
    }

    internal static List<JqValue> Scan(JqContext context, JqValue? input, JqValue? patternNode, JqValue? flagsNode)
    {
        JqString text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        string flags = RequireModifiers(context, flagsNode);
        if (flags.Length != 0) context.Budget.ReserveString(1L + flags.Length);
        return SearchText(context, text, pattern, "g" + flags, Projection.Scan);
    }

    internal static List<JqValue> Split(JqContext context, JqValue? input, JqValue? patternNode, JqValue? flagsNode)
    {
        JqString text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        string flags = RequireModifiers(context, flagsNode);
        if (flags.Length != 0) context.Budget.ReserveString(1L + flags.Length);
        return SearchText(context, text, pattern, flags + "g", Projection.Split);
    }
}
