using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using Lokad.Utf8Regex.Pcre2;
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
        JqRegexCache.Pattern compiled = context.Regexes.Get(pattern, options.Pattern);
        Pcre2MatchOptions matchOptions = options.Match;
        Dictionary<int, string> names = GroupNameMap(compiled);
        ReadOnlyMemory<byte> subject = context.Regexes.EncodeSubject(text);
        var results = new List<JsonObject>();
        int start = 0;
        while (true)
        {
            Utf8Pcre2MatchContext match = context.Regexes.Match(compiled, subject, start, matchOptions);
            if (!match.Success)
                break;
            context.Budget.ChargeNode();
            int utf16Start = match.Value.StartOffsetInUtf16;
            int utf16End = match.Value.EndOffsetInUtf16;
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

    internal static Dictionary<int, string> GroupNameMap(JqRegexCache.Pattern compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        return compiled.Names;
    }

    // Named-capture fold for sub/gsub replacements: numbered groups in
    // order with last-wins, including null for non-participating groups,
    // mirroring `def sub`'s reduce over .captures in builtin.jq. Duplicate
    // names (enabled via PCRE2_DUPNAMES) fold the same way.
    internal static JsonObject FoldNamedCaptures(JqContext context, Utf8Pcre2MatchContext match, Dictionary<int, string> names, int captureCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(names);
        var captures = new JsonObject();
        context.Budget.ChargeNode();
        for (int index = 1; index <= captureCount; index++)
        {
            if (!names.TryGetValue(index, out string? name) || name is null)
                continue;
            if (!match.TryGetGroup(index, out Utf8Pcre2GroupContext group))
                break;
            context.Budget.ChargeNode();
            context.Budget.ChargeString(name.Length);
            if (group.Success)
            {
                context.Budget.ChargeString(group.EndOffsetInUtf16 - group.StartOffsetInUtf16);
                string value = group.GetValueString();
                captures[name] = JsonValue.Create(value);
            }
            else
            {
                captures[name] = null;
            }
        }
        return captures;
    }

    private static JsonObject BuildMatch(JqContext context, string text, Utf8Pcre2MatchContext match, Dictionary<int, string> names, int captureCount)
    {
        context.Budget.ChargeString(match.Value.EndOffsetInUtf16 - match.Value.StartOffsetInUtf16);
        string whole = match.GetValueString();
        var result = new JsonObject
        {
            ["offset"] = JsonValue.Create(ScalarOffset(text, match.Value.StartOffsetInUtf16)),
            ["length"] = JsonValue.Create(ScalarOffset(text, match.Value.EndOffsetInUtf16) - ScalarOffset(text, match.Value.StartOffsetInUtf16)),
            ["string"] = JsonValue.Create(whole),
        };
        var captures = new JsonArray();
        for (int index = 1; index <= captureCount; index++)
        {
            if (!match.TryGetGroup(index, out Utf8Pcre2GroupContext group))
                break;
            context.Budget.ChargeNode();
            var capture = new JsonObject();
            if (!group.Success)
            {
                capture["offset"] = JsonValue.Create(-1);
                capture["length"] = JsonValue.Create(0);
                capture["string"] = null;
            }
            else
            {
                context.Budget.ChargeString(group.EndOffsetInUtf16 - group.StartOffsetInUtf16);
                string value = group.GetValueString();
                capture["offset"] = JsonValue.Create(ScalarOffset(text, group.StartOffsetInUtf16));
                capture["length"] = JsonValue.Create(ScalarOffset(text, group.EndOffsetInUtf16) - ScalarOffset(text, group.StartOffsetInUtf16));
                capture["string"] = JsonValue.Create(value);
            }
            if (names.TryGetValue(index, out string? name))
            {
                capture["name"] = JsonValue.Create(name);
                context.Budget.ChargeString(name.Length);
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
                    context.Budget.ChargeNode();
                    context.Budget.ChargeString(name.Length);
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
        context.Budget.ChargeString(message.Length);
        throw new JqErrorException(JsonValue.Create(message), "error: " + message);
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
        var results = new List<JsonNode?>();
        foreach (JsonObject match in SearchText(context, text, pattern, "g" + flags))
        {
            if (match["captures"] is JsonArray captures && captures.Count > 0)
            {
                var row = new JsonArray();
                foreach (JsonNode? capture in captures)
                {
                    context.Budget.ChargeNode();
                    row.Add(context.Runtime.Clone(capture is JsonObject item ? item["string"] : null));
                }
                results.Add(row);
            }
            else
            {
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
        var starts = new List<int>(text.Length + 1) { 0 };
        foreach (Rune rune in text.EnumerateRunes())
            starts.Add(starts[starts.Count - 1] + rune.Utf16SequenceLength);
        var pieces = new List<string>();
        int previous = 0;
        foreach (JsonObject match in matches)
        {
            int offset = MatchField(match, "offset");
            pieces.Add(SliceScalars(context, text, starts, previous, offset));
            previous = offset + MatchField(match, "length");
        }
        pieces.Add(SliceScalars(context, text, starts, previous, starts.Count - 1));
        return pieces;
    }

    private static int MatchField(JsonObject match, string name)
    {
        if (match[name] is JsonValue value && value.TryGetValue<int>(out int number))
            return number;
        throw new JqException("regex split produced a non-integer " + name);
    }

    private static string SliceScalars(JqContext context, string text, List<int> starts, int from, int to)
    {
        context.Budget.ChargeNode();
        string piece = text.Substring(starts[from], starts[to] - starts[from]);
        context.Budget.ChargeString(piece.Length);
        return piece;
    }
}
