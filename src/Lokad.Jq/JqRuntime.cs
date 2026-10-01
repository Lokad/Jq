using System;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed class JqRuntime(JqBudget budget)
{
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions AsciiJson = new() { Encoder = JavaScriptEncoder.Default };

    internal string Serialize(JsonNode? node, bool ascii, int? indent, bool tabs)
        => Encoding.UTF8.GetString(SerializeUtf8(node, ascii, indent, tabs).Span);

    internal ReadOnlyMemory<byte> SerializeUtf8(JsonNode? node, bool ascii, int? indent, bool tabs) =>
        SerializeUtf8(node, ascii, indent, tabs, sorted: false);

    internal ReadOnlyMemory<byte> SerializeUtf8(JsonNode? node, bool ascii, int? indent, bool tabs, bool sorted)
    {
        budget.ChargeTree(node);
        JsonNode? shaped = sorted ? SortedClone(node) : node;
        if (sorted)
            budget.ChargeTree(shaped);
        // The JSON writer rejects non-finite doubles while the reference
        // renders NaN as null and clamps infinities to the finite
        // extremes. Sanitize a charged clone only when needed.
        JsonNode? clean = ContainsNonFinite(shaped) ? SanitizeNonFinite(shaped) : shaped;
        var buffer = new JqJsonBuffer(budget);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = ascii ? JavaScriptEncoder.Default : JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = indent != null || tabs,
            IndentCharacter = tabs ? '\t' : ' ',
            IndentSize = tabs ? 1 : indent ?? 2,
            MaxDepth = JqBudget.MaximumDepth
        }))
        {
            if (clean == null) writer.WriteNullValue();
            else clean.WriteTo(writer, ascii ? AsciiJson : CompactJson);
            writer.Flush();
        }
        ReadOnlyMemory<byte> rendered = buffer.WrittenMemory;
        if (indent != null || tabs)
            rendered = NormalizeNewlines(rendered);
        budget.ChargeString(Encoding.UTF8.GetCharCount(rendered.Span));
        return rendered;
    }

    // Deep clone with object keys in ordinal order for `--sort-keys`.
    // Scalars and arrays keep their shape; only key order changes.
    internal static JsonNode? SortedClone(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var property in obj.OrderBy(property => property.Key, StringComparer.Ordinal))
                    sorted.Add(property.Key, SortedClone(property.Value));
                return sorted;
            case JsonArray array:
                var copy = new JsonArray();
                foreach (JsonNode? item in array)
                    copy.Add(SortedClone(item));
                return copy;
            default:
                return node.DeepClone();
        }
    }

    // The JSON writer emits platform newlines for indentation; jq output is
    // LF-only. Raw carriage returns cannot appear inside JSON string
    // literals (controls stay escaped), so dropping `\r` before `\n` only
    // touches structural newlines.
    private static ReadOnlyMemory<byte> NormalizeNewlines(ReadOnlyMemory<byte> body)
    {
        ReadOnlySpan<byte> span = body.Span;
        int pairs = 0;
        for (var i = 0; i + 1 < span.Length; i++)
        {
            if (span[i] == (byte)13 && span[i + 1] == (byte)10)
            {
                pairs++;
                i++;
            }
        }
        if (pairs == 0)
            return body;
        var normalized = new byte[span.Length - pairs];
        int written = 0;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)13 && i + 1 < span.Length && span[i + 1] == (byte)10)
                continue;
            normalized[written++] = span[i];
        }
        return normalized;
    }

    private static bool ContainsNonFinite(JsonNode? node)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<double>(out double number) && !double.IsFinite(number))
            return true;
        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
                if (ContainsNonFinite(child))
                    return true;
            return false;
        }
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
                if (ContainsNonFinite(property.Value))
                    return true;
            return false;
        }
        return false;
    }

    private JsonNode? SanitizeNonFinite(JsonNode? node)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<double>(out double number) && !double.IsFinite(number))
            return double.IsNaN(number) ? null : JsonValue.Create(number > 0 ? double.MaxValue : double.MinValue);
        JsonNode? copy = Clone(node);
        SanitizeInPlace(copy);
        return copy;
    }

    private static void SanitizeInPlace(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                JsonNode? child = array[index];
                if (child is JsonValue scalar && scalar.TryGetValue<double>(out double number) && !double.IsFinite(number))
                    array[index] = double.IsNaN(number) ? null : JsonValue.Create(number > 0 ? double.MaxValue : double.MinValue);
                else
                    SanitizeInPlace(child);
            }
        }
        else if (node is JsonObject obj)
        {
            List<string> keys = new List<string>(obj.Count);
            foreach (var property in obj)
                keys.Add(property.Key);
            foreach (string key in keys)
            {
                JsonNode? child = obj[key];
                if (child is JsonValue scalar && scalar.TryGetValue<double>(out double number) && !double.IsFinite(number))
                    obj[key] = double.IsNaN(number) ? null : JsonValue.Create(number > 0 ? double.MaxValue : double.MinValue);
                else
                    SanitizeInPlace(child);
            }
        }
    }

    internal JsonNode? Clone(JsonNode? node)
    {
        budget.ChargeTree(node);
        return node?.DeepClone();
    }

    // Single-pass decode: builds values while charging, so duplicate object
    // keys resolve last-wins at first position like object assignment.
    internal JsonNode? ReadJsonValue(ReadOnlySpan<byte> text, out int consumed)
    {
        try
        {
            var reader = new Utf8JsonReader(text, new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                AllowMultipleValues = true,
                MaxDepth = JqBudget.MaximumDepth
            });
            int firstContent = SkipJsonWhitespace(text, 0);
            if (TryMatchNonFinite(text, firstContent, out double topValue, out int topEnd))
            {
                budget.ChargeNode();
                consumed = topEnd;
                budget.ChargeBytes(consumed - firstContent);
                return JsonValue.Create(topValue);
            }
            if (TryInvalidNonFiniteLiteral(text, firstContent, out string invalidMessage))
                throw new JqException(invalidMessage);
            if (!reader.Read())
                throw new JqException("expected a JSON value");
            var start = (int)reader.TokenStartIndex;
            int consumedBase = 0;
            JsonNode? value = ReadValue(ref reader, text, ref consumedBase, 0);
            consumed = consumedBase + (int)reader.BytesConsumed;
            budget.ChargeBytes(consumed - start);
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new JqException(ex.Message);
        }
    }

    private static int SkipJsonWhitespace(ReadOnlySpan<byte> text, int position)
    {
        while (position < text.Length && text[position] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            position++;
        return position;
    }

    private static bool IsAsciiLetter(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    private static bool IsWord(ReadOnlySpan<byte> text, int start, int end, ReadOnlySpan<byte> word)
    {
        if (end - start != word.Length)
            return false;
        for (int i = 0; i < word.Length; i++)
        {
            byte candidate = text[start + i];
            if (candidate >= (byte)'A' && candidate <= (byte)'Z')
                candidate = (byte)(candidate + 32);
            if (candidate != word[i])
                return false;
        }
        return true;
    }

    private static bool IsValueBoundary(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)']' or (byte)'}';

    private static bool IsValuePreceded(ReadOnlySpan<byte> text, int position)
    {
        int index = position - 1;
        while (index >= 0 && text[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            index--;
        if (index < 0)
            return true;
        return text[index] is (byte)'[' or (byte)',' or (byte)':';
    }

    private static bool TryInvalidNonFiniteLiteral(ReadOnlySpan<byte> text, int position, out string message)
    {
        message = string.Empty;
        int index = SkipJsonWhitespace(text, position);
        if (index < text.Length && (text[index] == (byte)'+' || text[index] == (byte)'-'))
            index++;
        int word = index;
        while (index < text.Length && IsAsciiLetter(text[index]))
            index++;
        if (word == index)
            return false;
        if (!IsWord(text, word, index, "nan"u8) && !IsWord(text, word, index, "inf"u8) && !IsWord(text, word, index, "infinity"u8))
            return false;
        if (index >= text.Length || IsValueBoundary(text[index]))
            return false;
        int junk = index;
        while (junk < text.Length && IsLiteralByte(text[junk]))
            junk++;
        int tail = junk;
        while (tail < text.Length && text[tail] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            tail++;
        if (tail != text.Length)
            return false;
        ComputeEofPosition(text, out int line, out int column);
        message = "Invalid numeric literal at EOF at line " + line + ", column " + column;
        return true;
    }

    private static bool IsLiteralByte(byte value) =>
        value != (byte)' ' && value != (byte)'\t' && value != (byte)'\r' && value != (byte)'\n' &&
        value != (byte)'[' && value != (byte)',' && value != (byte)']' && value != (byte)'{' &&
        value != (byte)':' && value != (byte)'}' && value != (byte)'"';

    private static void ComputeEofPosition(ReadOnlySpan<byte> text, out int line, out int column)
    {
        line = 1;
        column = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == (byte)'\n')
            {
                line++;
                column = 0;
            }
            else
            {
                column++;
            }
        }
    }

    private static bool TryMatchNonFinite(ReadOnlySpan<byte> text, int position, out double value, out int end)
    {
        value = 0;
        end = position;
        int index = SkipJsonWhitespace(text, position);
        bool negative = false;
        if (index < text.Length && (text[index] == (byte)'+' || text[index] == (byte)'-'))
        {
            negative = text[index] == (byte)'-';
            index++;
        }
        int word = index;
        while (index < text.Length && IsAsciiLetter(text[index]))
            index++;
        double magnitude;
        if (IsWord(text, word, index, "nan"u8))
            magnitude = double.NaN;
        else if (IsWord(text, word, index, "inf"u8) || IsWord(text, word, index, "infinity"u8))
            magnitude = double.PositiveInfinity;
        else
            return false;
        if (index < text.Length && !IsValueBoundary(text[index]))
            return false;
        value = negative ? -magnitude : magnitude;
        end = index;
        return true;
    }

    // consumeComma mirrors the restored state kind: array starts and object values expect a value, so a trailing comma belongs to the resumed reader, while later array elements sit in a post-value state that consumes the comma itself. A resume preserves the state kind, so callers must not flip the flag on resumed values.
    private void ReadToken(ref Utf8JsonReader reader, ReadOnlySpan<byte> source, ref int consumedBase, JsonReaderState restoreState, bool consumeComma, out bool hasToken, out JsonNode? nonFinite)
        {
            nonFinite = null;
            try
            {
                hasToken = reader.Read();
            }
            catch (JsonException)
            {
                int failure = consumedBase + (int)reader.BytesConsumed;
                if (IsValuePreceded(source, failure)
                    && TryInvalidNonFiniteLiteral(source, failure, out string nestedInvalid))
                    throw new JqException(nestedInvalid);
                if (IsValuePreceded(source, failure)
                    && TryMatchNonFinite(source, failure, out double number, out int end))
                {
                    budget.ChargeNode();
                    int resume = SkipJsonWhitespace(source, end);
                    if (consumeComma && resume < source.Length && source[resume] == (byte)',')
                        resume++;
                    reader = new Utf8JsonReader(source.Slice(resume), reader.IsFinalBlock, restoreState);
                    consumedBase = resume;
                    hasToken = true;
                    nonFinite = JsonValue.Create(number);
                    return;
                }
                throw;
            }
        }

        JsonNode? ReadValue(ref Utf8JsonReader reader, ReadOnlySpan<byte> source, ref int consumedBase, int depth)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (depth >= JqBudget.MaximumDepth)
                        throw new JqException("value nesting limit exceeded");
                    budget.ChargeNode();
                    var obj = new JsonObject();
                    bool expectsObjectValue = true;
                    while (true)
                    {
                        JsonReaderState headState = reader.CurrentState;
                        if (!reader.Read())
                            throw new JqException("truncated JSON value");
                        if (reader.TokenType == JsonTokenType.EndObject)
                            return obj;
                        if (reader.TokenType != JsonTokenType.PropertyName)
                            throw new JqException("expected object key");
                        string? name = reader.GetString();
                        if (name is null)
                            throw new JqException("expected object key");
                        budget.ChargeString(name.Length);
                        ReadToken(ref reader, source, ref consumedBase, headState, expectsObjectValue, out bool hasValue, out JsonNode? valueFallback);
                        if (!hasValue)
                            throw new JqException("truncated JSON value");
                        if (valueFallback is not null)
                        {
                            // A resume preserves the restored state kind, so the flag stays put.
                            obj[name] = valueFallback;
                        }
                        else
                        {
                            obj[name] = ReadValue(ref reader, source, ref consumedBase, depth + 1);
                            expectsObjectValue = false;
                        }
                    }
                    throw new JqException("truncated JSON value");
                case JsonTokenType.StartArray:
                    if (depth >= JqBudget.MaximumDepth)
                        throw new JqException("value nesting limit exceeded");
                    budget.ChargeNode();
                    var array = new JsonArray();
                    bool expectsValue = true;
                    while (true)
                    {
                        JsonReaderState headState = reader.CurrentState;
                        ReadToken(ref reader, source, ref consumedBase, headState, expectsValue, out bool hasToken, out JsonNode? nonFinite);
                        if (!hasToken)
                            throw new JqException("truncated JSON value");
                        if (nonFinite is not null)
                        {
                            array.Add(nonFinite);
                            continue;
                        }
                        if (reader.TokenType == JsonTokenType.EndArray)
                            return array;
                        array.Add(ReadValue(ref reader, source, ref consumedBase, depth + 1));
                        expectsValue = false;
                    }
                case JsonTokenType.String:
                    budget.ChargeNode();
                    string? textValue = reader.GetString();
                    if (textValue is null)
                        throw new JqException("expected a JSON string");
                    budget.ChargeString(textValue.Length);
                    return JsonValue.Create(textValue);
                case JsonTokenType.Number:
                    budget.ChargeNode();
                    if (reader.TryGetInt64(out long whole))
                    {
                        if (whole == 0 && reader.ValueSpan.Length > 0 && reader.ValueSpan[0] == (byte)'-' )
                            return JsonValue.Create(reader.GetDouble());
                        return JsonValue.Create(whole);
                    }
                    return JsonValue.Create(reader.GetDouble());
                case JsonTokenType.True:
                    budget.ChargeNode();
                    return JsonValue.Create(true);
                case JsonTokenType.False:
                    budget.ChargeNode();
                    return JsonValue.Create(false);
                case JsonTokenType.Null:
                    budget.ChargeNode();
                    return null;
                default:
                    throw new JqException("expected a JSON value");
            }
        }

    internal JsonNode? ParseJson(string text)
    {
        budget.ChargeBytes(Encoding.UTF8.GetByteCount(text));
        var bytes = Encoding.UTF8.GetBytes(text);
        int prefix = HasBomPrefix(bytes) ? 3 : 0;
        int consumed;
        JsonNode? node;
        try
        {
            node = ReadJsonValue(bytes.AsSpan(prefix), out consumed);
        }
        catch (JqException ex) when (ex is not JqQuotaException && ex.Message.StartsWith("Invalid numeric literal at EOF", StringComparison.Ordinal))
        {
            throw new JqException(ex.Message + " (while parsing '" + text + "')");
        }
        consumed += prefix;
        if (!bytes.AsSpan(consumed).Trim(" \t\r\n"u8).IsEmpty)
            throw new JqException("expected a single JSON value");
        return node;
    }

    internal static bool HasBomPrefix(ReadOnlySpan<byte> text) =>
        text.Length >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF;

    // Kind-shaped operand diagnostics shared by arithmetic operators,
    // matching the reference operand rendering (operands dump as JSON, so
    // strings render quoted).
    internal string TypeError(JsonNode? l, JsonNode? r, string verb) =>
        $"{TypeName(l)} ({Serialize(l, false, null, false)}) and {TypeName(r)} ({Serialize(r, false, null, false)}) {verb}";

    internal JsonNode? Add(JsonNode? l, JsonNode? r)
    {
        if (l == null) return Clone(r);
        if (r == null) return Clone(l);
        if (TypeName(l) == "number" && TypeName(r) == "number")
            return JsonValue.Create(Number(l) + Number(r));
        if (TryGetString(l, out var ls) && TryGetString(r, out var rs))
        {
            budget.ChargeString((long)ls.Length + rs.Length);
            return JsonValue.Create(ls + rs);
        }
        if (l is JsonArray la && r is JsonArray ra)
        {
            var arr = new JsonArray();
            foreach (var v in la.Concat(ra)) arr.Add(Clone(v));
            return arr;
        }
        if (l is JsonObject lo && r is JsonObject ro)
        {
            if (Clone(lo) is not JsonObject obj)
                throw new InvalidOperationException("Expected object clone.");
            foreach (var kv in ro) obj[kv.Key] = Clone(kv.Value);
            return obj;
        }
        throw new JqRuntimeException(TypeError(l, r, "cannot be added"));
    }

    internal JsonNode? Subtract(JsonNode? l, JsonNode? r)
    {
        if (TypeName(l) == "number" && TypeName(r) == "number")
            return JsonValue.Create(Number(l) - Number(r));
        if (l is JsonArray la && r is JsonArray ra)
        {
            var result = new JsonArray();
            foreach (var item in la)
            {
                bool excluded = false;
                foreach (var needle in ra)
                {
                    if (JsonEquals(item, needle))
                    {
                        excluded = true;
                        break;
                    }
                }
                if (!excluded)
                    result.Add(Clone(item));
            }
            return result;
        }
        throw new JqRuntimeException(TypeError(l, r, "cannot be subtracted"));
    }

    internal JsonNode? Multiply(JsonNode? l, JsonNode? r)
    {
        if (TypeName(l) == "number" && TypeName(r) == "number")
            return JsonValue.Create(Number(l) * Number(r));
        if (TryGetString(l, out var leftText) && TypeName(r) == "number")
            return Repeat(leftText, Number(r));
        if (TryGetString(r, out var rightText) && TypeName(l) == "number")
            return Repeat(rightText, Number(l));
        if (l is JsonObject lo && r is JsonObject ro)
            return MergeRecursive(lo, ro, 0);
        throw new JqRuntimeException(TypeError(l, r, "cannot be multiplied"));
    }

    private JsonNode? Repeat(string value, double count)
    {
        // Truncation matches the reference repeat mapping; negative and NaN
        // counts produce null.
        int times = double.IsNaN(count) || count < 0 ? -1 : count > int.MaxValue ? int.MaxValue : (int)count;
        if (times < 0)
            return null;
        var length = (long)value.Length * times;
        budget.ChargeString(length);
        if (length == 0)
            return JsonValue.Create(string.Empty);
        if (times == 1)
            return JsonValue.Create(value);
        if (value.Length == 1)
            return JsonValue.Create(new string(value[0], times));

        return JsonValue.Create(string.Create((int)length, value, static (destination, text) =>
        {
            for (var offset = 0; offset < destination.Length; offset += text.Length)
                text.AsSpan().CopyTo(destination[offset..]);
        }));
    }

    internal JsonNode MergeRecursive(JsonObject left, JsonObject right, int depth)
    {
        if (depth > JqBudget.MaximumDepth)
            throw new JqException("value nesting limit exceeded");
        if (Clone(left) is not JsonObject merged)
            throw new InvalidOperationException("Expected object clone.");
        foreach (var property in right)
        {
            if (merged.TryGetPropertyValue(property.Key, out JsonNode? existing)
                && existing is JsonObject existingObject
                && property.Value is JsonObject incomingObject)
                merged[property.Key] = MergeRecursive(existingObject, incomingObject, depth + 1);
            else
                merged[property.Key] = Clone(property.Value);
        }
        return merged;
    }

    // Numeric projection with a string fallback. Callers decide strictness:
    // arithmetic, negation, math builtins, and halt codes kind-check first
    // (numeric strings stay rejected); count positions use TryCountLevel to
    // keep the recorded leniency; flatten depths and slice bounds use
    // TryDepthLevel to follow their reference kind rules instead.
    // Count positions keep the Number() leniency for numeric strings while
    // reporting kind failures with the caller-chosen diagnostic.
    internal static bool TryCountLevel(JsonNode? depth, out double level)
    {
        if (depth is JsonValue value)
        {
            if (value.TryGetValue<double>(out level))
                return true;
            if (value.TryGetValue<long>(out long whole))
            {
                level = whole;
                return true;
            }
            if (value.TryGetValue<int>(out int integer))
            {
                level = integer;
                return true;
            }
            if (value.TryGetValue<decimal>(out decimal dec))
            {
                level = (double)dec;
                return true;
            }
            if (value.TryGetValue<string>(out string? raw) &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                level = parsed;
                return true;
            }
        }
        level = double.NaN;
        return false;
    }

    internal static double Number(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        if (node is JsonValue i && i.TryGetValue<int>(out var intValue)) return intValue;
        if (node is JsonValue l && l.TryGetValue<long>(out var longValue)) return longValue;
        if (node is JsonValue m && m.TryGetValue<decimal>(out var decimalValue)) return (double)decimalValue;
        if (node is JsonValue s && s.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new JqRuntimeException($"expected number, got {TypeName(node)}");
    }

    internal JsonNode ToJsonNumber(JsonNode? node)
    {
        if (TypeName(node) == "number")
            return Clone(node) ?? JsonValue.Create(0);
        if (TryGetString(node, out string? text) && text is not null && !text.Contains('\0'))
        {
            if (TryParseStrictNumber(text, out JsonNode? number) && number is not null)
                return number;
            if (TryParseNonFiniteNumber(text, out JsonNode? nonFinite) && nonFinite is not null)
                return nonFinite;
        }
        throw new JqException($"{TypeName(node)} ({Serialize(node, false, null, false)}) cannot be parsed as a number");
    }

    // Non-finite spellings mirror the reference strtod fallback (builtin.c
    // f_tonumber through jvp_strtod INFNAN_CHECK): an optional sign with a
    // case-insensitive nan, inf, or infinity and no surrounding whitespace.
    // Hex floats and NaN payloads stay rejected.
    private static bool TryParseNonFiniteNumber(string text, out JsonNode? number)
    {
        number = null;
        ReadOnlySpan<char> rest = text;
        bool negative = false;
        if (rest.Length > 0 && (rest[0] == '+' || rest[0] == '-'))
        {
            negative = rest[0] == '-';
            rest = rest.Slice(1);
        }
        if (rest.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            number = JsonValue.Create(double.NaN);
            return true;
        }
        if (rest.Equals("inf", StringComparison.OrdinalIgnoreCase) ||
            rest.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            number = JsonValue.Create(negative ? double.NegativeInfinity : double.PositiveInfinity);
            return true;
        }
        return false;
    }

    // Strict JSON-number grammar with an optional leading sign and no
    // surrounding whitespace. Integral shapes keep integral storage.
    private static bool TryParseStrictNumber(string text, out JsonNode? number)
    {
        number = null;
        int position = 0;
        if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            position++;
        int whole = position;
        while (position < text.Length && char.IsAsciiDigit(text[position]))
            position++;
        bool hasWhole = position > whole;
        bool hasFraction = false;
        if (position < text.Length && text[position] == '.')
        {
            position++;
            int fraction = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
                position++;
            hasFraction = position > fraction;
            if (!hasFraction)
                return false;
        }
        if (!hasWhole && !hasFraction)
            return false;
        if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
        {
            position++;
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
                position++;
            int exponent = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
                position++;
            if (position == exponent)
                return false;
        }
        if (position != text.Length)
            return false;
        if (!hasFraction && text.IndexOfAny(new[] { 'e', 'E' }) < 0 && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long wholeValue))
        {
            number = JsonValue.Create(wholeValue);
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            number = JsonValue.Create(value);
            return true;
        }
        return false;
    }

    internal bool ToJsonBoolean(JsonNode? node)
    {
        if (node is JsonValue boolean && boolean.TryGetValue<bool>(out bool value))
            return value;
        if (TryGetString(node, out string? text) && text is not null && !text.Contains('\0'))
        {
            if (text == "true")
                return true;
            if (text == "false")
                return false;
        }
        throw new JqException($"{TypeName(node)} ({Serialize(node, false, null, false)}) cannot be parsed as a boolean");
    }

    internal static int Utf8ByteLength(JsonNode? input, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TryGetString(input, out string? text) && text is not null)
            return System.Text.Encoding.UTF8.GetByteCount(text);
        throw new JqException($"{TypeName(input)} ({context.Runtime.Serialize(input, false, null, false)}) only strings have UTF-8 byte length");
    }


    internal static string String(JsonNode? node)
    {
        if (TryGetString(node, out var s)) return s;
        throw new JqRuntimeException($"expected string, got {TypeName(node)}");
    }

    internal string ToJqString(JsonNode? node)
    {
        if (node == null) return "null";
        if (TryGetString(node, out var s)) return s;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        // Integral storage prints exactly; doubles use the shortest
        // round-trip form, while non-finite values render as JSON.
        if (node is JsonValue i && i.TryGetValue<int>(out var intValue)) return intValue.ToString(CultureInfo.InvariantCulture);
        if (node is JsonValue l && l.TryGetValue<long>(out var longValue)) return longValue.ToString(CultureInfo.InvariantCulture);
        if (node is JsonValue m && m.TryGetValue<decimal>(out var decimalValue)) return decimalValue.ToString(CultureInfo.InvariantCulture);
        if (node is JsonValue n && n.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d.ToString(CultureInfo.InvariantCulture) : Serialize(node, false, null, false);
        return Serialize(node, false, null, false);
    }

    internal static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            value = s;
            return true;
        }
        return false;
    }

    // Array reads accept any numeric key like the reference jv_get: NaN
    // reads null elsewhere, other values clamp to int range and truncate
    // toward zero, negatives resolve from the end, and out-of-range reads
    // yield null. Probe each integral storage in turn since conversions
    // are storage-strict for created values.
    internal static bool TryGetArrayIndex(JsonNode? node, out long index)
    {
        index = 0;
        if (node is JsonValue small && small.TryGetValue<int>(out int direct))
        {
            index = direct;
            return true;
        }
        if (node is JsonValue whole && whole.TryGetValue<long>(out long directLong))
        {
            index = directLong;
            return true;
        }
        if (node is JsonValue real && real.TryGetValue<double>(out double value) && !double.IsNaN(value))
        {
            if (value < int.MinValue)
                index = int.MinValue;
            else if (value > int.MaxValue)
                index = int.MaxValue;
            else
                index = (long)value;
            return true;
        }
        return false;
    }

    // JsonValue conversions are storage-strict for created values but
    // convertible for parsed ones, so probe each integral storage in turn.
    internal static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is JsonValue v && v.TryGetValue<int>(out int direct))
        {
            value = direct;
            return true;
        }
        if (node is JsonValue l && l.TryGetValue<long>(out long whole) && whole >= int.MinValue && whole <= int.MaxValue)
        {
            value = (int)whole;
            return true;
        }
        if (node is JsonValue d && d.TryGetValue<double>(out var number))
        {
            value = (int)number;
            return Math.Abs(number - value) < double.Epsilon;
        }
        return false;
    }

    internal static bool Truthy(JsonNode? node)
    {
        if (node == null) return false;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return true;
    }

    internal bool JsonEquals(JsonNode? l, JsonNode? r)
    {
        budget.ChargeTree(l);
        budget.ChargeTree(r);
        return EqualsValue(l, r);
    }

    // Mirrors the reference value equality: kind-sensitive, objects
    // order-insensitive, arrays ordered, numbers by double value with
    // NaN unequal to everything including itself.
    private static bool EqualsValue(JsonNode? left, JsonNode? right) => EqualsValue(left, right, 0);

    // Depth-bounded like the budget tree walk: values deeper than the policy
    // fail cleanly instead of consuming CLR frames.
    private static bool EqualsValue(JsonNode? left, JsonNode? right, int depth)
    {
        if (depth > JqBudget.MaximumDepth)
            throw new JqQuotaException("value nesting limit exceeded");
        if (left is null || right is null)
            return left is null && right is null;
        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count)
                return false;
            for (var index = 0; index < leftArray.Count; index++)
                if (!EqualsValue(leftArray[index], rightArray[index], depth + 1))
                    return false;
            return true;
        }
        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count)
                return false;
            foreach (var property in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(property.Key, out JsonNode? other))
                    return false;
                if (!EqualsValue(property.Value, other, depth + 1))
                    return false;
            }
            return true;
        }
        if (TryGetString(left, out string leftText) && TryGetString(right, out string rightText))
            return string.Equals(leftText, rightText, StringComparison.Ordinal);
        if (left is JsonValue leftScalar && right is JsonValue rightScalar
            && leftScalar.TryGetValue<bool>(out bool leftBool) && rightScalar.TryGetValue<bool>(out bool rightBool))
            return leftBool == rightBool;
        if (TypeName(left) == "number" && TypeName(right) == "number")
            return Number(left) == Number(right);
        return false;
    }

    // Total order shared by comparison operators and min/max: null, false,
    // true, numbers, strings (Unicode scalar order), arrays (lexical),
    // objects (sorted keys, then values). NaN orders immediately after null
    // and before every number (like the reference kind difference for null
    // versus NaN, with null substituted elsewhere per jv_aux.c jvp_cmp) but
    // never equals anything, matching the reference.
    internal static int Compare(JsonNode? l, JsonNode? r) => Compare(l, r, 0);

    // Depth-bounded like the budget tree walk: values deeper than the policy
    // fail cleanly instead of consuming CLR frames. Every ordering path
    // (operators, sort, unique, min/max, bsearch, group) funnels through here.
    private static int Compare(JsonNode? l, JsonNode? r, int depth)
    {
        if (depth > JqBudget.MaximumDepth)
            throw new JqQuotaException("value nesting limit exceeded");
        if (l is null && IsNaNNumber(r))
            return -1;
        if (r is null && IsNaNNumber(l))
            return 1;
        int leftRank = ValueRank(l);
        int rightRank = ValueRank(r);
        if (leftRank != rightRank)
            return leftRank.CompareTo(rightRank);
        if (l is JsonArray leftArray && r is JsonArray rightArray)
        {
            int shared = Math.Min(leftArray.Count, rightArray.Count);
            for (var index = 0; index < shared; index++)
            {
                int order = Compare(leftArray[index], rightArray[index], depth + 1);
                if (order != 0)
                    return order;
            }
            return leftArray.Count.CompareTo(rightArray.Count);
        }
        if (l is JsonObject leftObject && r is JsonObject rightObject)
        {
            List<string> leftKeys = new List<string>(leftObject.Count);
            foreach (var property in leftObject)
                leftKeys.Add(property.Key);
            List<string> rightKeys = new List<string>(rightObject.Count);
            foreach (var property in rightObject)
                rightKeys.Add(property.Key);
            leftKeys.Sort(CompareScalars);
            rightKeys.Sort(CompareScalars);
            int shared = Math.Min(leftKeys.Count, rightKeys.Count);
            for (var index = 0; index < shared; index++)
            {
                int order = CompareScalars(leftKeys[index], rightKeys[index]);
                if (order != 0)
                    return order;
            }
            if (leftKeys.Count != rightKeys.Count)
                return leftKeys.Count.CompareTo(rightKeys.Count);
            foreach (string key in leftKeys)
            {
                int order = Compare(leftObject[key], rightObject[key], depth + 1);
                if (order != 0)
                    return order;
            }
            return 0;
        }
        if (TryGetString(l, out string leftText) && TryGetString(r, out string rightText))
            return CompareScalars(leftText, rightText);
        if (TypeName(l) == "number" && TypeName(r) == "number")
            return Number(l).CompareTo(Number(r));
        return 0;
    }

    private static int ValueRank(JsonNode? node)
    {
        if (node is null || IsNaNNumber(node))
            return 0;
        if (node is JsonValue scalar && scalar.TryGetValue<bool>(out bool boolean))
            return boolean ? 2 : 1;
        if (TypeName(node) == "number")
            return 3;
        if (TryGetString(node, out _))
            return 4;
        if (node is JsonArray)
            return 5;
        return 6;
    }

    private static bool IsNaNNumber(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<double>(out double number) && double.IsNaN(number);

    // Ordinal comparison by Unicode scalar value, matching byte order
    // for valid UTF-8. UTF-16 unit order would misorder BMP suffix
    // characters against supplementary characters.
    private static int CompareScalars(string left, string right)
    {
        ReadOnlySpan<char> first = left.AsSpan();
        ReadOnlySpan<char> second = right.AsSpan();
        while (!first.IsEmpty && !second.IsEmpty)
        {
            System.Text.Rune.DecodeFromUtf16(first, out System.Text.Rune leftRune, out int leftLength);
            System.Text.Rune.DecodeFromUtf16(second, out System.Text.Rune rightRune, out int rightLength);
            if (leftRune.Value != rightRune.Value)
                return leftRune.Value.CompareTo(rightRune.Value);
            first = first[leftLength..];
            second = second[rightLength..];
        }
        return first.IsEmpty && second.IsEmpty ? 0 : (first.IsEmpty ? -1 : 1);
    }

    internal JsonNode? Length(JsonNode? node) => node switch
    {
        null => JsonValue.Create(0),
        JsonArray arr => JsonValue.Create(arr.Count),
        JsonObject obj => JsonValue.Create(obj.Count),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(s.EnumerateRunes().Count()),
        // Like the reference number branch, the length of a number is its
        // absolute value; anything else has no length.
        JsonValue v when TypeName(v) == "number" => JsonValue.Create(Math.Abs(Number(v))),
        _ => throw new JqException($"{TypeName(node)} ({Serialize(node, false, null, false)}) has no length"),
    };

    internal static string TypeName(JsonNode? node)
    {
        if (node == null) return "null";
        if (node is JsonObject) return "object";
        if (node is JsonArray) return "array";
        if (node is JsonValue v && v.TryGetValue<bool>(out _)) return "boolean";
        if (node is JsonValue s && s.TryGetValue<string>(out _)) return "string";
        return "number";
    }

    internal JsonNode Keys(JsonNode? input, bool sorted)
    {
        if (input is JsonObject obj)
        {
            var names = new List<string>();
            foreach (var property in obj)
                names.Add(property.Key);
            if (sorted)
                names.Sort(StringComparer.Ordinal);
            var keys = new JsonArray();
            foreach (string name in names)
            {
                budget.ChargeNode();
                keys.Add(JsonValue.Create(name));
            }
            return keys;
        }
        if (input is JsonArray arr)
        {
            var keys = new JsonArray();
            for (int index = 0; index < arr.Count; index++)
            {
                budget.ChargeNode();
                keys.Add(JsonValue.Create(index));
            }
            return keys;
        }
        throw new JqException($"{TypeName(input)} ({Serialize(input, false, null, false)}) has no keys");
    }

    internal bool Has(JsonNode? input, JsonNode? key)
    {
        if (input is null)
            return false;
        if (input is JsonObject obj && TryGetString(key, out string? name))
            return obj.ContainsKey(name);
        if (input is JsonArray arr && JqPaths.TryGetIndex(key, out long index, out bool isNaN))
        {
            if (isNaN)
                return false;
            long clamped = index < int.MinValue ? int.MinValue : index > int.MaxValue ? int.MaxValue : index;
            return clamped >= 0 && clamped < arr.Count;
        }
        throw new JqException($"Cannot check whether {TypeName(input)} has a {TypeName(key)} key");
    }

    internal JsonArray ToEntries(JsonNode? input)
    {
        var entries = new JsonArray();
        if (input is JsonObject obj)
        {
            foreach (var property in obj)
            {
                budget.ChargeNode();
                var entry = new JsonObject();
                entry["key"] = JsonValue.Create(property.Key);
                entry["value"] = Clone(property.Value);
                entries.Add(entry);
            }
            return entries;
        }
        if (input is JsonArray arr)
        {
            for (int index = 0; index < arr.Count; index++)
            {
                budget.ChargeNode();
                var entry = new JsonObject();
                entry["key"] = JsonValue.Create(index);
                entry["value"] = Clone(arr[index]);
                entries.Add(entry);
            }
            return entries;
        }
        throw new JqException($"{TypeName(input)} ({Serialize(input, false, null, false)}) has no keys");
    }

    internal JsonNode FromEntries(JsonNode? input)
    {
        if (input is not JsonArray arr)
            throw new JqRuntimeException($"cannot iterate over {TypeName(input)}");
        var result = new JsonObject();
        foreach (JsonNode? element in arr)
        {
            if (element is not JsonObject entry)
                throw new JqRuntimeException($"cannot index {TypeName(element)} with string \"key\"");
            JsonNode? key = null;
            foreach (string name in new[] { "key", "Key", "name", "Name" })
            {
                if (entry.TryGetPropertyValue(name, out JsonNode? candidate) && Truthy(candidate))
                {
                    key = candidate;
                    break;
                }
            }
            if (key is null || !TryGetString(key, out string? keyName) || keyName is null)
            {
                if (key is null)
                    continue;
                throw new JqException($"Cannot use {TypeName(key)} ({ToJqString(key)}) as object key");
            }
            budget.ChargeNode();
            JsonNode? value = entry.TryGetPropertyValue("value", out JsonNode? direct) ? direct
                : entry.TryGetPropertyValue("Value", out JsonNode? capitalized) ? capitalized : null;
            result[keyName] = Clone(value);
        }
        return result;
    }

    internal JsonNode? AddValues(IEnumerable<JsonNode?> values)
    {
        JsonNode? result = null;
        foreach (JsonNode? value in values)
            result = Add(result, value);
        return result;
    }

    internal JsonNode Flatten(JsonNode? input, double depth)
    {
        // Like the reference reduce over `.[]`, null iterates empty and
        // objects contribute their values; other scalars fail.
        var result = new JsonArray();
        budget.ChargeNode();
        if (input is null)
            return result;
        if (input is JsonArray arr)
        {
            foreach (JsonNode? child in arr)
                FlattenInto(result, child, depth);
            return result;
        }
        if (input is JsonObject obj)
        {
            foreach (var property in obj)
                FlattenInto(result, property.Value, depth);
            return result;
        }
        throw new JqRuntimeException($"cannot iterate over {TypeName(input)}");
    }

    private void FlattenInto(JsonArray result, JsonNode? node, double depth)
    {
        // Explicit stack: deeply nested inputs must not consume CLR frames.
        // Children queue in order with the same per-level depth accounting as
        // the reference reduce; leaves keep the single node charge each.
        var pending = new List<(JsonNode? Node, double Depth)> { (node, depth) };
        while (pending.Count > 0)
        {
            var (current, level) = pending[pending.Count - 1];
            pending.RemoveAt(pending.Count - 1);
            if (current is JsonArray nested && level != 0)
            {
                for (int index = nested.Count - 1; index >= 0; index--)
                    pending.Add((nested[index], level - 1));
                continue;
            }
            budget.ChargeNode();
            result.Add(Clone(current));
        }
    }

    internal JsonArray SortArray(JsonNode? input)
    {
        if (input is not JsonArray arr)
            throw new JqException("cannot be sorted, as it is not an array");
        var ordered = new List<JsonNode?>(arr.Count);
        foreach (JsonNode? element in arr)
        {
            budget.ChargeNode();
            ordered.Add(element);
        }
        var result = new JsonArray();
        budget.ChargeNode();
        foreach (JsonNode? element in ordered.OrderBy(static element => element, Comparer<JsonNode?>.Create((left, right) => Compare(left, right))))
        {
            budget.ChargeNode();
            result.Add(Clone(element));
        }
        return result;
    }

    internal JsonNode UniqueArray(JsonNode? input)
    {
        if (input is not JsonArray)
            throw new JqException("cannot be sorted, as it is not an array");
        var ordered = new List<JsonNode?>();
        foreach (JsonNode? element in SortArray(input))
        {
            if (ordered.Count == 0 || !JsonEquals(ordered[ordered.Count - 1], element))
                ordered.Add(element);
        }
        var result = new JsonArray();
        budget.ChargeNode();
        foreach (JsonNode? element in ordered)
        {
            budget.ChargeNode();
            result.Add(Clone(element));
        }
        return result;
    }

    internal JsonNode? MinMax(JsonNode? input, bool max)
    {
        if (input is not JsonArray arr)
            throw new JqException("cannot be iterated over");
        if (arr.Count == 0) return null;
        var best = arr[0];
        foreach (var item in arr.Skip(1))
        {
            if ((max && Compare(item, best) >= 0) || (!max && Compare(item, best) < 0))
                best = item;
        }
        return Clone(best);
    }

    internal JsonNode Reverse(JsonNode? input)
    {
        if (TryGetString(input, out var s))
        {
            budget.ChargeString(s.Length);
            return JsonValue.Create(string.Create(s.Length, s, static (target, source) =>
            {
                source.AsSpan().CopyTo(target);
                target.Reverse();
            }));
        }
        if (input is JsonArray arr)
        {
            var rev = new JsonArray();
            for (var i = arr.Count - 1; i >= 0; i--) rev.Add(Clone(arr[i]));
            return rev;
        }
        throw new JqRuntimeException($"cannot reverse {TypeName(input)}");
    }

    internal bool Contains(JsonNode? container, JsonNode? contained)
    {
        if (TypeName(container) != TypeName(contained))
            throw new JqException($"{TypeName(container)} ({Serialize(container, false, null, false)}) and {TypeName(contained)} ({Serialize(contained, false, null, false)}) cannot have their containment checked");
        return ContainsSameKind(container, contained);
    }

    private bool ContainsSameKind(JsonNode? container, JsonNode? contained) => ContainsSameKind(container, contained, 0);

    // Depth-bounded like the budget tree walk: values deeper than the policy
    // fail cleanly instead of consuming CLR frames.
    private bool ContainsSameKind(JsonNode? container, JsonNode? contained, int depth)
    {
        if (depth > JqBudget.MaximumDepth)
            throw new JqQuotaException("value nesting limit exceeded");
        if (container is JsonObject obj && contained is JsonObject needle)
            return needle.All(kv => obj.TryGetPropertyValue(kv.Key, out var value) && ContainsSameKind(value, kv.Value, depth + 1));
        if (container is JsonArray haystack && contained is JsonArray needles)
            return needles.All(needle => haystack.Any(candidate => ContainsSameKind(candidate, needle, depth + 1)));
        if (TryGetString(container, out var text) && TryGetString(contained, out var fragment))
            return fragment.Length == 0 || text.Contains(fragment, StringComparison.Ordinal);
        return JsonEquals(container, contained);
    }

    internal JsonNode Indices(JsonNode? input, JsonNode? needle)
    {
        var arr = new JsonArray();
        foreach (var index in MatchingIndices(input, needle)) arr.Add(JsonValue.Create(index));
        return arr;
    }

    internal JsonNode? Index(JsonNode? input, JsonNode? needle)
    {
        foreach (var index in MatchingIndices(input, needle)) return JsonValue.Create(index);
        return null;
    }

    private IEnumerable<int> MatchingIndices(JsonNode? input, JsonNode? needle)
    {
        if (TryGetString(input, out var text) && text is not null && TryGetString(needle, out var fragment))
        {
            // Scalar offsets, matching rune-wise substring search. Like the
            // reference byte search, an empty needle matches nowhere and the
            // scan resumes one scalar past each hit so overlaps are reported.
            var runes = new List<System.Text.Rune>();
            foreach (var rune in text.EnumerateRunes())
                runes.Add(rune);
            var wanted = new List<System.Text.Rune>();
            if (fragment is not null)
                foreach (var rune in fragment.EnumerateRunes())
                    wanted.Add(rune);
            if (wanted.Count == 0)
                yield break;
            for (int start = 0; start + wanted.Count <= runes.Count; start++)
            {
                bool match = true;
                for (int offset = 0; offset < wanted.Count; offset++)
                    if (!runes[start + offset].Equals(wanted[offset]))
                    {
                        match = false;
                        break;
                    }
                if (!match)
                    continue;
                budget.ChargeNode();
                yield return start;
            }
            yield break;
        }
        if (input is JsonArray values)
        {
            if (needle is JsonArray pattern)
            {
                // Contiguous subsequence search; like the reference
                // jv_array_indexes, an empty needle matches nowhere (the
                // string branch already yields nothing too).
                if (pattern.Count == 0)
                    yield break;
                for (int start = 0; start + pattern.Count <= values.Count; start++)
                {
                    bool match = true;
                    for (int offset = 0; offset < pattern.Count; offset++)
                        if (!JsonEquals(values[start + offset], pattern[offset]))
                        {
                            match = false;
                            break;
                        }
                    if (!match)
                        continue;
                    budget.ChargeNode();
                    yield return start;
                    start += Math.Max(0, pattern.Count - 1);
                }
                yield break;
            }
            for (var i = 0; i < values.Count; i++)
                if (JsonEquals(values[i], needle))
                {
                    budget.ChargeNode();
                    yield return i;
                }
            yield break;
        }
        throw new JqRuntimeException($"cannot search {TypeName(input)}");
    }

    internal string AsciiCase(string text, bool lower)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (lower && ch >= 'A' && ch <= 'Z')
                builder.Append((char)(ch + 32));
            else if (!lower && ch >= 'a' && ch <= 'z')
                builder.Append((char)(ch - 32));
            else
                builder.Append(ch);
        }
        budget.ChargeString(builder.Length);
        return budget.Finish(builder);
    }

    internal static bool StartsEndsWith(JsonNode? input, JsonNode? affix, bool fromStart)
    {
        if (!TryGetString(input, out string? text) || text is null || !TryGetString(affix, out string? fix) || fix is null)
            throw new JqException((fromStart ? "startswith" : "endswith") + "() requires string inputs");
        return fromStart
            ? text.StartsWith(fix, StringComparison.Ordinal)
            : text.EndsWith(fix, StringComparison.Ordinal);
    }

    internal static string TrimAffix(JsonNode? input, JsonNode? affix, bool left, bool right)
    {
        if (!TryGetString(input, out string? text) || text is null || !TryGetString(affix, out string? fix) || fix is null)
            throw new JqException((left ? "startswith" : "endswith") + "() requires string inputs");
        if (left && text.StartsWith(fix, StringComparison.Ordinal))
            text = text[fix.Length..];
        if (right && text.EndsWith(fix, StringComparison.Ordinal))
            text = text[..^fix.Length];
        return text;
    }

    internal static bool IsJqWhitespace(int codepoint) =>
        (codepoint is >= 0x09 and <= 0x0D)
        || codepoint == 0x20
        || codepoint == 0x85
        || codepoint == 0xA0
        || codepoint == 0x1680
        || codepoint is >= 0x2000 and <= 0x200A
        || codepoint == 0x2028
        || codepoint == 0x2029
        || codepoint == 0x202F
        || codepoint == 0x205F
        || codepoint == 0x3000;

    internal static string TrimSides(JsonNode? input, bool left, bool right)
    {
        if (!TryGetString(input, out string? text) || text is null)
            throw new JqException("trim input must be a string");
        var units = new List<(int Offset, int Length, bool Whitespace)>();
        int cursor = 0;
        while (cursor < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(cursor), out Rune rune, out int consumed) == System.Buffers.OperationStatus.Done)
                units.Add((cursor, consumed, IsJqWhitespace(rune.Value)));
            else
                units.Add((cursor, 1, false));
            cursor += units[units.Count - 1].Length;
        }
        int first = 0;
        int last = units.Count;
        if (left)
            while (first < last && units[first].Whitespace)
                first++;
        if (right)
            while (last > first && units[last - 1].Whitespace)
                last--;
        int start = first < units.Count ? units[first].Offset : text.Length;
        int end = last > 0 ? units[last - 1].Offset + units[last - 1].Length : 0;
        return text[start..end];
    }

    internal JsonNode Explode(JsonNode? input)
    {
        if (!TryGetString(input, out string? text) || text is null)
            throw new JqException("explode input must be a string");
        var arr = new JsonArray();
        foreach (var rune in text.EnumerateRunes())
        {
            budget.ChargeNode();
            arr.Add(rune.Value);
        }
        return arr;
    }

    internal JsonNode Implode(JsonNode? input)
    {
        if (input is not JsonArray arr)
            throw new JqException("implode input must be an array");
        var sb = new StringBuilder();
        foreach (JsonNode? item in arr)
        {
            long point;
            if (item is JsonValue small && small.TryGetValue<int>(out int direct))
                point = direct;
            else if (item is JsonValue number && number.TryGetValue<long>(out long whole))
                point = whole;
            else if (item is JsonValue real && real.TryGetValue<double>(out double value) && !double.IsNaN(value))
                point = (long)value;
            else
                throw new JqException($"{TypeName(item)} ({Serialize(item, false, null, false)}) can't be imploded, unicode codepoint needs to be numeric");
            int scalar = point < 0 || point > 0x10FFFF || (point >= 0xD800 && point <= 0xDFFF) ? 0xFFFD : (int)point;
            budget.Append(sb, char.ConvertFromUtf32(scalar).AsSpan());
        }
        return JsonValue.Create(budget.Finish(sb));
    }

    internal JsonNode Split(JsonNode? input, JsonNode? separator)
    {
        if (!TryGetString(input, out string? text) || text is null || !TryGetString(separator, out string? delimiter) || delimiter is null)
            throw new JqException("split input and separator must be strings");
        var arr = new JsonArray();
        if (text.Length == 0)
            return arr;
        if (delimiter.Length == 0)
        {
            // An empty separator splits into Unicode scalars.
            foreach (var rune in text.EnumerateRunes())
            {
                budget.ChargeNode();
                budget.ChargeString(rune.Utf16SequenceLength);
                arr.Add(rune.ToString());
            }
            return arr;
        }
        var start = 0;
        while (true)
        {
            var end = text.IndexOf(delimiter, start, StringComparison.Ordinal);
            var length = (end < 0 ? text.Length : end) - start;
            budget.ChargeNode();
            budget.ChargeString(length);
            arr.Add(text.Substring(start, length));
            if (end < 0) break;
            start = end + delimiter.Length;
        }
        return arr;
    }

    internal JsonNode Join(JsonNode? input, JsonNode? separator)
    {
        if (input is null)
            return JsonValue.Create(string.Empty);
        if (input is not JsonArray arr)
            throw new JqRuntimeException("cannot iterate over " + TypeName(input));
        bool hasSepText = separator is null;
        string sepText = string.Empty;
        if (separator is not null && TryGetString(separator, out string? sepCandidate) && sepCandidate is not null)
        {
            hasSepText = true;
            sepText = sepCandidate;
        }
        var builder = new StringBuilder();
        bool first = true;
        foreach (JsonNode? item in arr)
        {
            if (!first)
            {
                if (!hasSepText)
                {
                    JsonNode accNode = JsonValue.Create(builder.ToString());
                    throw new JqRuntimeException(TypeName(accNode) + " (" + Serialize(accNode, false, null, false) + ") and " + TypeName(separator) + " (" + Serialize(separator, false, null, false) + ") cannot be added");
                }
                budget.Append(builder, sepText);
            }
            string elemText;
            if (item is null)
            {
                elemText = string.Empty;
            }
            else if (TryGetString(item, out string? s) && s is not null)
            {
                elemText = s;
            }
            else if (TypeName(item) == "boolean" || TypeName(item) == "number")
            {
                elemText = ToJqString(item);
            }
            else
            {
                JsonNode leftNode = JsonValue.Create(builder.ToString());
                throw new JqRuntimeException(TypeName(leftNode) + " (" + Serialize(leftNode, false, null, false) + ") and " + TypeName(item) + " (" + Serialize(item, false, null, false) + ") cannot be added");
            }
            budget.Append(builder, elemText);
            first = false;
        }
        return JsonValue.Create(budget.Finish(builder));
    }

    internal IEnumerable<JsonNode?> Range(IReadOnlyList<List<JsonNode?>> args)
    {
        if (args.Count is < 1 or > 3)
            throw new JqException("range expects one to three arguments");
        if (args.Any(arg => arg.Count == 0))
            yield break;

        // Upstream range/3 steps through doubles (fractional steps count) and
        // yields nothing for a zero step; range/1..2 always steps by one.
        // Integral outputs keep integral storage like literals and inputs.
        double start = args.Count == 1 ? 0 : RangeBound(args[0][0]);
        double end = RangeBound(args.Count == 1 ? args[0][0] : args[1][0]);
        double step = args.Count > 2 ? RangeBound(args[2][0]) : 1;
        if (args.Count > 2 && !(step > 0) && !(step < 0))
            yield break;

        for (double value = start; step > 0 ? value < end : value > end; value += step)
        {
            budget.ChargeNode();
            yield return CreateNumber(value);
        }
    }

    // Like the reference RANGE opcode, non-numeric bounds fail while numeric
    // strings keep the recorded count leniency.
    private static double RangeBound(JsonNode? bound)
    {
        if (!TryCountLevel(bound, out double value))
            throw new JqException("Range bounds must be numeric");
        return value;
    }

    // Numbers that are whole and fit in a long keep integral storage like
    // literals and inputs; anything else stays a double.
    internal static JsonNode? CreateNumber(double value)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value) && value == Math.Truncate(value) && value >= long.MinValue && value <= long.MaxValue)
            return JsonValue.Create((long)value);
        return JsonValue.Create(value);
    }

    internal string Format(string format, JsonNode? value)
    {
        if (format == "json") return Serialize(value, false, null, false);
        if (format == "csv") return Csv(value);
        if (format == "tsv") return Tsv(value);
        if (format == "sh") return Sh(value);
        if (format == "urid") return UriDecode(ToJqString(value), value);
        var text = ToJqString(value);
        switch (format)
        {
            case "text": return text;
            case "html": return Html(text);
            case "uri": return EncodeChunks(Uri.EscapeDataString);
            case "base64":
            {
                var bytes = Encoding.UTF8.GetByteCount(text);
                budget.ChargeString(4 * ((bytes + 2L) / 3));
                budget.ChargeBytes(bytes);
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            }
            case "base64d": return Base64Decode(text, value);
            default: throw new JqException($"unsupported format @{format}");
        }

        string EncodeChunks(Func<string, string> encode)
        {
            var builder = new StringBuilder();
            for (var start = 0; start < text.Length;)
            {
                var count = Math.Min(1024, text.Length - start);
                if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1])) count--;
                // Encoders work on bounded chunks; never split a surrogate pair at a chunk boundary.
                budget.ChargeBytes(24L * count);
                budget.Append(builder, encode(text.Substring(start, count)));
                start += count;
            }
            return budget.Finish(builder);
        }

        // Upstream escapes only ampersand, angle brackets, quote and apostrophe,
        // leaving UTF-8 bytes raw. Codes: 38 is &, 60 is <, 62 is >, 39 is apostrophe, 34 is quote.
        string Html(string source)
        {
            var builder = new StringBuilder();
            foreach (char ch in source)
            {
                if (ch == (char)38) budget.Append(builder, "&amp;");
                else if (ch == (char)60) budget.Append(builder, "&lt;");
                else if (ch == (char)62) budget.Append(builder, "&gt;");
                else if (ch == (char)39) budget.Append(builder, "&apos;");
                else if (ch == (char)34) budget.Append(builder, "&quot;");
                else budget.Append(builder, new ReadOnlySpan<char>(in ch));
            }
            return budget.Finish(builder);
        }

        // Manual decoder matching the reference: stops at the first padding mark,
        // rejects non-alphabet bytes, and reports a dangling single quantum.
        // Codes: 61 is =, 65 to 90 are A-Z, 97 to 122 are a-z, 48 to 57 are 0-9, 43 is +, 47 is /.
        string Base64Decode(string source, JsonNode? original)
        {
            budget.ChargeBytes(2L * source.Length + 8);
            var decoded = new List<byte>(source.Length);
            uint code = 0;
            int pending = 0;
            for (int index = 0; index < source.Length && source[index] != (char)61; index++)
            {
                if ((index & 4095) == 0) budget.CheckCancellation();
                int digit = Base64Value(source[index]);
                if (digit < 0)
                    throw new JqException(TypeName(original) + " (" + Serialize(original, false, null, false) + ") is not valid base64 data");
                code = (code << 6) | (uint)digit;
                pending++;
                if (pending == 4)
                {
                    decoded.Add((byte)((code >> 16) & 0xFF));
                    decoded.Add((byte)((code >> 8) & 0xFF));
                    decoded.Add((byte)(code & 0xFF));
                    pending = 0;
                    code = 0;
                }
            }
            if (pending == 3)
            {
                decoded.Add((byte)((code >> 10) & 0xFF));
                decoded.Add((byte)((code >> 2) & 0xFF));
            }
            else if (pending == 2)
            {
                decoded.Add((byte)((code >> 4) & 0xFF));
            }
            else if (pending == 1)
            {
                throw new JqException(TypeName(original) + " (" + Serialize(original, false, null, false) + ") trailing base64 byte found");
            }
            byte[] bytes = decoded.ToArray();
            budget.ChargeString(Encoding.UTF8.GetCharCount(bytes));
            // Invalid UTF-8 becomes U+FFFD, matching the reference replacement.
            return Encoding.UTF8.GetString(bytes);
        }

        int Base64Value(char ch)
        {
            if (ch >= (char)65 && ch <= (char)90) return ch - 65;
            if (ch >= (char)97 && ch <= (char)122) return ch - 97 + 26;
            if (ch >= (char)48 && ch <= (char)57) return ch - 48 + 52;
            if (ch == (char)43) return 62;
            if (ch == (char)47) return 63;
            return -1;
        }

        // Strict percent decoder: a dangling mark, short or non-hex digits,
        // and bytes that are not valid UTF-8 all fail with the same diagnostic.
        // Code 37 is percent.
        string UriDecode(string source, JsonNode? original)
        {
            budget.ChargeBytes(3L * source.Length + 8);
            var decoded = new List<byte>(source.Length);
            for (int index = 0; index < source.Length; index++)
            {
                if ((index & 4095) == 0) budget.CheckCancellation();
                char ch = source[index];
                if (ch == (char)37)
                {
                    if (index + 2 >= source.Length)
                        throw new JqException(TypeName(original) + " (" + Serialize(original, false, null, false) + ") is not a valid uri encoding");
                    int hi = HexValue(source[index + 1]);
                    int lo = HexValue(source[index + 2]);
                    if (hi < 0 || lo < 0)
                        throw new JqException(TypeName(original) + " (" + Serialize(original, false, null, false) + ") is not a valid uri encoding");
                    decoded.Add((byte)((hi << 4) | lo));
                    index += 2;
                }
                else if (ch < (char)128)
                {
                    decoded.Add((byte)ch);
                }
                else
                {
                    // Non-ASCII scalars pass through as their UTF-8 bytes.
                    int length = char.IsHighSurrogate(ch) && index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]) ? 2 : 1;
                    byte[] raw = Encoding.UTF8.GetBytes(source.Substring(index, length));
                    decoded.AddRange(raw);
                    index += length - 1;
                }
            }
            byte[] bytes = decoded.ToArray();
            budget.ChargeString(bytes.Length);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new JqException(TypeName(original) + " (" + Serialize(original, false, null, false) + ") is not a valid uri encoding");
            }
        }

        int HexValue(char ch)
        {
            if (ch >= (char)48 && ch <= (char)57) return ch - 48;
            if (ch >= (char)97 && ch <= (char)102) return ch - 97 + 10;
            if (ch >= (char)65 && ch <= (char)70) return ch - 65 + 10;
            return -1;
        }

        // Scalars are wrapped as single-element rows; only strings take quotes.
        string Sh(JsonNode? input)
        {
            if (input is JsonArray items)
            {
                var builder = new StringBuilder();
                for (int index = 0; index < items.Count; index++)
                {
                    if (index > 0) budget.Append(builder, " ");
                    AppendShell(builder, items[index]);
                }
                return budget.Finish(builder);
            }
            var single = new StringBuilder();
            AppendShell(single, input);
            return budget.Finish(single);
        }

        void AppendShell(StringBuilder builder, JsonNode? element)
        {
            if (element is null || TypeName(element) == "boolean" || TypeName(element) == "number")
            {
                budget.Append(builder, Serialize(element, false, null, false));
                return;
            }
            if (TryGetString(element, out string? raw) && raw is not null)
            {
                budget.Append(builder, "'");
                foreach (char ch in raw)
                    budget.Append(builder, ch == (char)39 ? "'\\''" : new ReadOnlySpan<char>(in ch));
                budget.Append(builder, "'");
                return;
            }
            throw new JqException(TypeName(element) + " (" + Serialize(element, false, null, false) + ") can not be escaped for shell");
        }

        // String fields are always quoted with embedded quotes doubled.
        string Csv(JsonNode? rows)
        {
            if (rows is not JsonArray array)
                throw new JqException(TypeName(rows) + " (" + Serialize(rows, false, null, false) + ") cannot be csv-formatted, only array");
            var builder = new StringBuilder();
            for (int index = 0; index < array.Count; index++)
            {
                if (index > 0) budget.Append(builder, ",");
                JsonNode? field = array[index];
                if (field is null || IsNaNNumber(field)) continue;
                if (TypeName(field) == "boolean" || TypeName(field) == "number")
                {
                    budget.Append(builder, Serialize(field, false, null, false));
                }
                else if (TryGetString(field, out string? cell) && cell is not null)
                {
                    budget.Append(builder, "\"");
                    budget.Append(builder, cell.Replace("\"", "\"\""));
                    budget.Append(builder, "\"");
                }
                else
                {
                    throw new JqException(TypeName(field) + " (" + Serialize(field, false, null, false) + ") is not valid in a csv row");
                }
            }
            return budget.Finish(builder);
        }

        string Tsv(JsonNode? rows)
        {
            if (rows is not JsonArray array)
                throw new JqException(TypeName(rows) + " (" + Serialize(rows, false, null, false) + ") cannot be tsv-formatted, only array");
            var builder = new StringBuilder();
            for (var i = 0; i < array.Count; i++)
            {
                if (i > 0) budget.Append(builder, "\t");
                var field = array[i];
                if (field is null || IsNaNNumber(field)) continue;
                if (field is JsonArray or JsonObject)
                    throw new JqException(TypeName(field) + " (" + Serialize(field, false, null, false) + ") is not valid in a csv row");

                ReadOnlySpan<char> remaining = ToJqString(field);
                while (true)
                {
                    var escape = remaining.IndexOfAny("\t\n\r\\");
                    if (escape < 0)
                    {
                        budget.Append(builder, remaining);
                        break;
                    }
                    budget.Append(builder, remaining[..escape]);
                    budget.Append(builder, remaining[escape] switch
                    {
                        '\t' => "\\t",
                        '\n' => "\\n",
                        '\r' => "\\r",
                        _ => "\\\\"
                    });
                    remaining = remaining[(escape + 1)..];
                }
            }
            return budget.Finish(builder);
        }
    }
}
