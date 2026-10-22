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
    internal static List<JsonObject> Search(JqContext context, JsonNode? input, JsonNode? patternNode, JsonNode? modifiersNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        string text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        return SearchText(context, text, pattern, RequireModifiers(context, modifiersNode));
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

    internal static List<JsonObject> SearchText(JqContext context, string text, string pattern, string modifiers)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(modifiers);
        JqRegexOptions options = JqRegexOptions.ParseOrThrow(modifiers);
        bool global = modifiers.Contains('g');
        IJqCompiledRegex compiled = context.Regexes.Get(pattern, options.Pattern);
        JqRegexMatchOptions matchOptions = options.Match;
        IReadOnlyDictionary<int, string> names = JqRegexCache.GetGroupNames(compiled);
        using var ownedSubject = context.Regexes.EncodeSubject(text);
        ReadOnlyMemory<byte> subject = ownedSubject.Value;
        context.Budget.ReserveMetadata(32, 1);
        var results = new List<JsonObject>();
        int start = 0;
        while (true)
        {
            using var match = context.Regexes.Match(compiled, subject, start, matchOptions);
            if (!match.Success)
                break;
            context.Budget.CheckCancellation();
            int utf16Start = match.Value.StartOffsetInUtf16;
            int utf16End = match.Value.EndOffsetInUtf16;
            if (results.Count == results.Capacity)
            {
                int previous = results.Capacity;
                int capacity = JqBudget.BufferCapacity((long)results.Count + 1, Math.Max(4, 2L * previous));
                context.Budget.ReserveBytes(8L * capacity);
                results.EnsureCapacity(capacity);
                context.Budget.TemporaryStorage?.DiscardBytes(8L * previous);
            }
            results.Add(BuildMatch(context, text, match, names, match.CaptureSlotCount - 1));
            if (!global)
                break;
            if (utf16End == utf16Start)
            {
                // An empty match at the end is the last one; otherwise move one scalar past its end.
                if (utf16End == text.Length)
                    break;
                start = match.Value.EndOffsetInBytes + Encoding.UTF8.GetByteCount(text.AsSpan(utf16End, ScalarWidth(text, utf16End)));
            }
            else
                start = match.Value.EndOffsetInBytes;
            if (start > subject.Length)
                break;
        }
        return results;
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

    private static JsonObject BuildMatch(JqContext context, string text, JqRegexMatch match, IReadOnlyDictionary<int, string> names, int captureCount)
    {
        // Retained DOM headers, dictionaries, scalar wrappers and capture slots;
        // payload copies are admitted separately below. Keep this construction
        // estimate with the record instead of recounting the completed tree.
        context.Budget.ReserveMetadata(704 + 728L * captureCount, (int)Math.Min(32, 5L + 5L * captureCount));
        context.Budget.ReserveString(match.Value.EndOffsetInUtf16 - match.Value.StartOffsetInUtf16);
        string whole = text.Substring(match.Value.StartOffsetInUtf16, match.Value.EndOffsetInUtf16 - match.Value.StartOffsetInUtf16);
        var result = new JsonObject
        {
            ["offset"] = JsonValue.Create(ScalarOffset(text, match.Value.StartOffsetInUtf16)),
            ["length"] = JsonValue.Create(ScalarOffset(text, match.Value.EndOffsetInUtf16) - ScalarOffset(text, match.Value.StartOffsetInUtf16)),
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
                capture["offset"] = JsonValue.Create(ScalarOffset(text, group.StartOffsetInUtf16));
                capture["length"] = JsonValue.Create(ScalarOffset(text, group.EndOffsetInUtf16) - ScalarOffset(text, group.StartOffsetInUtf16));
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

    internal static int ScalarOffset(string text, int utf16Index)
    {
        int scalars = 0;
        int cursor = 0;
        while (cursor < utf16Index && cursor < text.Length)
        {
            scalars++;
            cursor += ScalarWidth(text, cursor);
        }
        return scalars;
    }

    private static int ScalarWidth(string text, int cursor) =>
        char.IsHighSurrogate(text[cursor]) && cursor + 1 < text.Length && char.IsLowSurrogate(text[cursor + 1]) ? 2 : 1;

    // Scan streams one value per global match: the capture strings when the
    // pattern defines groups, otherwise the whole match text.
    internal static List<JsonNode?> Scan(JqContext context, JsonNode? input, JsonNode? patternNode, JsonNode? flagsNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        string text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        string flags = RequireModifiers(context, flagsNode);
        context.Budget.ReserveMetadata(32, 1);
        var results = new List<JsonNode?>();
        foreach (JsonObject match in SearchText(context, text, pattern, "g" + flags))
        {
            if (results.Count == results.Capacity)
            {
                int previous = results.Capacity;
                int capacity = JqBudget.BufferCapacity((long)results.Count + 1, Math.Max(4, 2L * previous));
                context.Budget.ReserveBytes(8L * capacity);
                results.EnsureCapacity(capacity);
                context.Budget.TemporaryStorage?.DiscardBytes(8L * previous);
            }
            if (match["captures"] is JsonArray captures && captures.Count > 0)
            {
                context.Budget.ReserveMetadata(64 + 88L * captures.Count, (int)Math.Min(32, 1L + captures.Count));
                var row = new JsonArray();
                foreach (JsonNode? capture in captures)
                {
                    context.Budget.CheckCancellation();
                    row.Add(context.Runtime.Clone(capture is JsonObject item ? item["string"] : null));
                }
                results.Add(row);
            }
            else
            {
                context.Budget.ReserveMetadata(64, 1);
                results.Add(context.Runtime.Clone(match["string"]));
            }
        }
        return results;
    }

    // Gap strings between global matches plus the final tail, sliced by scalar.
    internal static List<string> SplitPieces(JqContext context, JsonNode? input, JsonNode? patternNode, JsonNode? flagsNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        string text = RequireText(context, input);
        string pattern = RequirePattern(context, patternNode);
        string flags = RequireModifiers(context, flagsNode);
        List<JsonObject> matches = SearchText(context, text, pattern, flags + "g");
        int capacity = JqBudget.BufferCapacity((long)matches.Count + 1, (long)matches.Count + 1);
        context.Budget.ReserveMetadata(32 + 8L * capacity, 1);
        var pieces = new List<string>(capacity);
        int previous = 0;
        int scalar = 0;
        int cursor = 0;
        foreach (JsonObject match in matches)
        {
            int offset = MatchField(match, "offset");
            int start = AdvanceTo(offset);
            pieces.Add(Slice(previous, start));
            previous = AdvanceTo(offset + MatchField(match, "length"));
        }
        pieces.Add(Slice(previous, text.Length));
        return pieces;

        int AdvanceTo(int target)
        {
            context.Budget.CheckCancellation();
            while (scalar < target && cursor < text.Length)
            {
                if ((scalar & 4095) == 0) context.Budget.CheckCancellation();
                cursor += ScalarWidth(text, cursor);
                scalar++;
            }
            return cursor;
        }

        string Slice(int from, int to)
        {
            context.Budget.ReserveString(to - from);
            return text.Substring(from, to - from);
        }
    }

    private static int MatchField(JsonObject match, string name)
    {
        if (match[name] is JsonValue value && value.TryGetValue<int>(out int number))
            return number;
        throw new JqException("regex split produced a non-integer " + name);
    }

}
