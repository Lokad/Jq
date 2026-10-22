using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// Shared upstream-shaped regex search behind match, capture, scan, splits,
// and sub. Offsets and lengths count Unicode scalars, and global iteration
// advances by scalar after empty matches. Matches are collected eagerly
// like the reference, which builds one array per call.
internal static class JqMatch
{
    internal static bool Test(JqContext context, JsonNode? input, JsonNode? patternNode, JsonNode? flagsNode)
    {
        string text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        JqRegexOptions options = JqRegexOptions.ParseOrThrow(RequireModifiers(context, flagsNode));
        var regex = context.Regexes.Get(pattern, options.Pattern);
        using var subject = context.Regexes.EncodeSubject(text);
        return context.Regexes.IsMatch(regex, subject.Value, options.Match);
    }

    internal static List<JsonNode> Search(JqContext context, JsonNode? input, JsonNode? patternNode, JsonNode? modifiersNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        string text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        return SearchText(context, text, pattern, RequireModifiers(context, modifiersNode), Projection.Match);
    }

    internal static string RequirePattern(JqContext context, JsonNode? patternNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetString(patternNode, out string? pattern) || pattern is null)
            throw new JqException(TypeName(patternNode) + " (" + context.Runtime.Serialize(patternNode, false, null, false) + ") is not a string");
        return pattern;
    }

    internal static string RequireText(JqContext context, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetString(input, out string? text) || text is null)
            throw new JqException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") cannot be matched, as it is not a string");
        return text;
    }

    internal static string RequireModifiers(JqContext context, JsonNode? modifiersNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (modifiersNode is null)
            return string.Empty;
        if (TryGetString(modifiersNode, out string? flags) && flags is not null)
            return flags;
        throw new JqException(TypeName(modifiersNode) + " (" + context.Runtime.Serialize(modifiersNode, false, null, false) + ") is not a string");
    }

    private enum Projection { Match, Scan, Split }

    private static List<JsonNode> SearchText(JqContext context, string text, string pattern, string modifiers, Projection projection)
    {
        JqRegexOptions options = JqRegexOptions.ParseOrThrow(modifiers);
        bool global = modifiers.Contains('g');
        IJqCompiledRegex compiled = context.Regexes.Get(pattern, options.Pattern);
        var names = projection == Projection.Match ? JqRegexCache.GetGroupNames(compiled) : null;
        using var ownedSubject = context.Regexes.EncodeSubject(text);
        ReadOnlyMemory<byte> subject = ownedSubject.Value;
        context.Budget.ReserveMetadata(32, 1);
        var results = new List<JsonNode>();
        var position = new ScalarPosition(text, context.Budget);
        int previousSplitEnd = 0;
        int start = 0;
        while (true)
        {
            using var match = context.Regexes.Match(compiled, subject, start, options.Match);
            if (!match.Success) break;
            context.Budget.CheckCancellation();
            int utf16Start = match.Value.StartOffsetInUtf16;
            int utf16End = match.Value.EndOffsetInUtf16;
            switch (projection)
            {
                case Projection.Match:
                    AddResult(BuildMatch(context, text, match,
                        names ?? throw new InvalidOperationException("Expected regex group metadata."),
                        match.CaptureSlotCount - 1, ref position));
                    break;
                case Projection.Scan:
                    AddResult(ScanMatch(match));
                    break;
                case Projection.Split:
                    AddResult(Slice(previousSplitEnd, utf16Start));
                    previousSplitEnd = utf16End;
                    break;
            }
            if (!global) break;
            if (utf16End == utf16Start)
            {
                if (utf16End == text.Length) break;
                start = match.Value.EndOffsetInBytes + Encoding.UTF8.GetByteCount(text.AsSpan(utf16End, ScalarWidth(text, utf16End)));
            }
            else start = match.Value.EndOffsetInBytes;
            if (start > subject.Length) break;
        }
        if (projection == Projection.Split) AddResult(Slice(previousSplitEnd, text.Length));
        return results;

        void AddResult(JsonNode value)
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

        JsonNode ScanMatch(JqRegexMatch match)
        {
            int captures = match.CaptureSlotCount - 1;
            if (captures == 0) return Slice(match.Value.StartOffsetInUtf16, match.Value.EndOffsetInUtf16);
            context.Budget.ReserveMetadata(64 + 88L * captures, (int)Math.Min(32, 1L + captures));
            var row = new JsonArray();
            for (int index = 1; index <= captures; index++)
            {
                context.Budget.CheckCancellation();
                if (!match.TryGetGroup(index, out JqRegexCapture group)) break;
                row.Add(group.Success ? JsonValue.Create(CopyString(group.StartOffsetInUtf16, group.EndOffsetInUtf16)) : null);
            }
            return row;
        }

        JsonNode Slice(int from, int to)
        {
            context.Budget.ReserveMetadata(64, 1);
            return JsonValue.Create(CopyString(from, to));
        }

        string CopyString(int from, int to)
        {
            if (from == 0 && to == text.Length) return text;
            context.Budget.ReserveString(to - from);
            return text.Substring(from, to - from);
        }
    }

    // Named-capture fold for sub/gsub replacements: numbered groups in
    // order with last-wins, including null for non-participating groups,
    // mirroring `def sub`'s reduce over .captures in builtin.jq. Duplicate
    // names (enabled via PCRE2_DUPNAMES) fold the same way.
    internal static JsonObject FoldNamedCaptures(JqContext context, string text, JqRegexMatch match, IReadOnlyDictionary<int, string> names, int captureCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(names);
        context.Budget.ReserveMetadata(64 + 224L * names.Count, (int)Math.Min(32, 1L + 2L * names.Count));
        var captures = new JsonObject();
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
                context.Budget.ReserveString(group.EndOffsetInUtf16 - group.StartOffsetInUtf16);
                string value = text.Substring(group.StartOffsetInUtf16, group.EndOffsetInUtf16 - group.StartOffsetInUtf16);
                captures[name] = JsonValue.Create(value);
            }
            else
            {
                captures[name] = null;
            }
        }
        return captures;
    }

    private static JsonObject BuildMatch(JqContext context, string text, JqRegexMatch match, IReadOnlyDictionary<int, string> names, int captureCount, ref ScalarPosition position)
    {
        // Retained DOM headers, dictionaries, scalar wrappers and capture slots;
        // payload copies are admitted separately below. Keep this construction
        // estimate with the record instead of recounting the completed tree.
        context.Budget.ReserveMetadata(704 + 728L * captureCount, (int)Math.Min(32, 5L + 5L * captureCount));
        context.Budget.ReserveString(match.Value.EndOffsetInUtf16 - match.Value.StartOffsetInUtf16);
        string whole = text.Substring(match.Value.StartOffsetInUtf16, match.Value.EndOffsetInUtf16 - match.Value.StartOffsetInUtf16);
        int offset = position.AdvanceTo(match.Value.StartOffsetInUtf16);
        ScalarPosition captureBase = position;
        int end = position.AdvanceTo(match.Value.EndOffsetInUtf16);
        var result = new JsonObject
        {
            ["offset"] = JsonValue.Create(offset),
            ["length"] = JsonValue.Create(end - offset),
            ["string"] = JsonValue.Create(whole),
        };
        var captures = new JsonArray();
        for (int index = 1; index <= captureCount; index++)
        {
            if (!match.TryGetGroup(index, out JqRegexCapture group))
                break;
            context.Budget.CheckCancellation();
            var capture = new JsonObject();
            if (!group.Success)
            {
                capture["offset"] = JsonValue.Create(-1);
                capture["length"] = JsonValue.Create(0);
                capture["string"] = null;
            }
            else
            {
                context.Budget.ReserveString(group.EndOffsetInUtf16 - group.StartOffsetInUtf16);
                string value = text.Substring(group.StartOffsetInUtf16, group.EndOffsetInUtf16 - group.StartOffsetInUtf16);
                ScalarPosition capturePosition = captureBase;
                int captureOffset = capturePosition.AdvanceTo(group.StartOffsetInUtf16);
                capture["offset"] = JsonValue.Create(captureOffset);
                capture["length"] = JsonValue.Create(capturePosition.AdvanceTo(group.EndOffsetInUtf16) - captureOffset);
                capture["string"] = JsonValue.Create(value);
            }
            if (names.TryGetValue(index, out string? name))
            {
                capture["name"] = JsonValue.Create(name);
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

    internal static JsonObject ToNamedObject(JqContext context, JsonObject match)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(match);
        context.Budget.ReserveMetadata(64, 1);
        var result = new JsonObject();
        if (match["captures"] is JsonArray captures)
        {
            foreach (JsonNode? item in captures)
            {
                if (item is JsonObject capture
                    && capture["name"] is JsonValue nameValue
                    && nameValue.TryGetValue<string>(out string? name)
                    && name is not null)
                {
                    context.Budget.CheckCancellation();
                    context.Budget.ReserveMetadata(224, 2);
                    result[name] = context.Runtime.Clone(capture["string"]);
                }
            }
        }
        return result;
    }

    internal static void SplitArgument(JqContext context, JsonNode? value, out JsonNode? pattern, out JsonNode? modifiers)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (value is JsonArray array)
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
        else if (TryGetString(value, out string? text) && text is not null)
        {
            pattern = value;
            modifiers = null;
            return;
        }
        string message = TypeName(value) + " not a string or array";
        context.Budget.ReserveString(message.Length);
        throw new JqErrorException(JsonValue.Create(message));
    }

    // Whole matches advance monotonically. Capture walks borrow that starting
    // position; a lookbehind can rewind without disturbing the search cursor.
    private struct ScalarPosition(string text, JqBudget budget)
    {
        private int _utf16;
        private int _scalars;

        internal int AdvanceTo(int target)
        {
            budget.CheckCancellation();
            if (target < _utf16) { _utf16 = 0; _scalars = 0; }
            while (_utf16 < target && _utf16 < text.Length)
            {
                if ((_scalars & 4095) == 0) budget.CheckCancellation();
                _utf16 += ScalarWidth(text, _utf16);
                _scalars++;
            }
            return _scalars;
        }
    }

    private static int ScalarWidth(string text, int cursor) =>
        char.IsHighSurrogate(text[cursor]) && cursor + 1 < text.Length && char.IsLowSurrogate(text[cursor + 1]) ? 2 : 1;

    internal static List<JsonNode> Scan(JqContext context, JsonNode? input, JsonNode? patternNode, JsonNode? flagsNode)
    {
        string text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        string flags = RequireModifiers(context, flagsNode);
        if (flags.Length != 0) context.Budget.ReserveString(1L + flags.Length);
        return SearchText(context, text, pattern, "g" + flags, Projection.Scan);
    }

    internal static List<JsonNode> Split(JqContext context, JsonNode? input, JsonNode? patternNode, JsonNode? flagsNode)
    {
        string text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        string flags = RequireModifiers(context, flagsNode);
        if (flags.Length != 0) context.Budget.ReserveString(1L + flags.Length);
        return SearchText(context, text, pattern, flags + "g", Projection.Split);
    }
}
