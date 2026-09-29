using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PCRE;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// Shared upstream-shaped regex search behind match, capture, and later
// scan, splits, and sub. Offsets and lengths count Unicode scalars, and
// global iteration advances by scalar after empty matches. Matches are
// collected eagerly like the reference, which builds one array per call.
internal static class JqMatch
{
    internal static List<JsonObject> Search(JqContext context, JsonNode? input, JsonNode? patternNode, JsonNode? modifiersNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetString(input, out string? text) || text is null)
            throw new JqException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") cannot be matched, as it is not a string");
        if (!TryGetString(patternNode, out string? pattern) || pattern is null)
            throw new JqException(TypeName(patternNode) + " (" + context.Runtime.Serialize(patternNode, false, null, false) + ") is not a string");
        string modifiers;
        if (modifiersNode is null)
            modifiers = string.Empty;
        else if (TryGetString(modifiersNode, out string? flags) && flags is not null)
            modifiers = flags;
        else
            throw new JqException(TypeName(modifiersNode) + " (" + context.Runtime.Serialize(modifiersNode, false, null, false) + ") is not a string");
        if (!JqRegexOptions.TryParse(modifiers, out JqRegexOptions options, out _))
            throw new JqException(modifiers + " is not a valid modifier string");
        bool global = modifiers.Contains('g');
        JqRegexCache.Pattern compiled = context.Regexes.Get(pattern, options.Pattern);
        PcreMatchOptions matchOptions = options.Match;
        var names = new Dictionary<int, string>();
        foreach (string name in compiled.Regex.PatternInfo.GroupNames)
            foreach (int index in compiled.Regex.PatternInfo.GetGroupIndexesByName(name))
                names[index] = name;
        int captureCount = compiled.Regex.PatternInfo.CaptureCount;
        var results = new List<JsonObject>();
        int start = 0;
        while (true)
        {
            PcreRefMatch match = context.Regexes.Match(compiled, text, start, matchOptions);
            if (!match.Success)
                break;
            // The first search validates the whole immutable string; later
            // suffix searches skip repeated UTF checks, as in substitutions.
            matchOptions |= PcreMatchOptions.NoUtfCheck;
            context.Budget.ChargeNode();
            int utf16Start = match.Index;
            int utf16End = match.EndIndex;
            results.Add(BuildMatch(context, text, match, names, captureCount));
            if (!global)
                break;
            if (utf16End == utf16Start)
            {
                // An empty match at the end is the last one; otherwise move one scalar past its end.
                if (utf16End == text.Length)
                    break;
                start = utf16End + ScalarWidth(text, utf16End);
            }
            else
                start = utf16End;
            if (start > text.Length)
                break;
        }
        return results;
    }

    private static JsonObject BuildMatch(JqContext context, string text, PcreRefMatch match, Dictionary<int, string> names, int captureCount)
    {
        string whole = match.Value.ToString();
        context.Budget.ChargeString(whole.Length);
        var result = new JsonObject
        {
            ["offset"] = JsonValue.Create(ScalarOffset(text, match.Index)),
            ["length"] = JsonValue.Create(ScalarOffset(text, match.EndIndex) - ScalarOffset(text, match.Index)),
            ["string"] = JsonValue.Create(whole),
        };
        var captures = new JsonArray();
        for (int index = 1; index <= captureCount; index++)
        {
            if (!match.TryGetGroup(index, out PcreRefGroup group))
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
                string value = group.Value.ToString();
                context.Budget.ChargeString(value.Length);
                capture["offset"] = JsonValue.Create(ScalarOffset(text, group.Index));
                capture["length"] = JsonValue.Create(ScalarOffset(text, group.EndIndex) - ScalarOffset(text, group.Index));
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
}
