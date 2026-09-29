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

    internal ReadOnlyMemory<byte> SerializeUtf8(JsonNode? node, bool ascii, int? indent, bool tabs)
    {
        budget.ChargeTree(node);
        // The JSON writer rejects non-finite doubles while the reference
        // renders NaN as null and clamps infinities to the finite
        // extremes. Sanitize a charged clone only when needed.
        JsonNode? clean = ContainsNonFinite(node) ? SanitizeNonFinite(node) : node;
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
        budget.ChargeString(Encoding.UTF8.GetCharCount(buffer.WrittenMemory.Span));
        return buffer.WrittenMemory;
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
            if (!reader.Read())
                throw new JqException("expected a JSON value");
            var start = (int)reader.TokenStartIndex;
            JsonNode? value = ReadValue(ref reader, 0);
            consumed = (int)reader.BytesConsumed;
            budget.ChargeBytes(consumed - start);
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new JqException(ex.Message);
        }

        JsonNode? ReadValue(ref Utf8JsonReader reader, int depth)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (depth >= JqBudget.MaximumDepth)
                        throw new JqException("value nesting limit exceeded");
                    budget.ChargeNode();
                    var obj = new JsonObject();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndObject)
                            return obj;
                        if (reader.TokenType != JsonTokenType.PropertyName)
                            throw new JqException("expected object key");
                        string? name = reader.GetString();
                        if (name is null)
                            throw new JqException("expected object key");
                        budget.ChargeString(name.Length);
                        if (!reader.Read())
                            throw new JqException("truncated JSON value");
                        obj[name] = ReadValue(ref reader, depth + 1);
                    }
                    throw new JqException("truncated JSON value");
                case JsonTokenType.StartArray:
                    if (depth >= JqBudget.MaximumDepth)
                        throw new JqException("value nesting limit exceeded");
                    budget.ChargeNode();
                    var array = new JsonArray();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndArray)
                            return array;
                        array.Add(ReadValue(ref reader, depth + 1));
                    }
                    throw new JqException("truncated JSON value");
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
    }

    internal JsonNode? ParseJson(string text)
    {
        budget.ChargeBytes(Encoding.UTF8.GetByteCount(text));
        var bytes = Encoding.UTF8.GetBytes(text);
        var node = ReadJsonValue(bytes, out var consumed);
        if (!bytes.AsSpan(consumed).Trim(" \t\r\n"u8).IsEmpty)
            throw new JqException("expected a single JSON value");
        return node;
    }

    // Kind-shaped operand diagnostics shared by arithmetic operators,
    // matching the reference operand rendering.
    internal string TypeError(JsonNode? l, JsonNode? r, string verb) =>
        $"{TypeName(l)} ({ToJqString(l)}) and {TypeName(r)} ({ToJqString(r)}) {verb}";

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

    internal static double Number(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        if (node is JsonValue i && i.TryGetValue<int>(out var intValue)) return intValue;
        if (node is JsonValue l && l.TryGetValue<long>(out var longValue)) return longValue;
        if (node is JsonValue m && m.TryGetValue<decimal>(out var decimalValue)) return (double)decimalValue;
        if (node is JsonValue s && s.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new JqRuntimeException($"expected number, got {TypeName(node)}");
    }

    internal static double ToNumber(JsonNode? node) => Number(node);

    internal static bool ToBoolean(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        if (node is JsonValue s && s.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)) return parsed;
        throw new JqRuntimeException($"expected boolean, got {TypeName(node)}");
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
    private static bool EqualsValue(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count)
                return false;
            for (var index = 0; index < leftArray.Count; index++)
                if (!EqualsValue(leftArray[index], rightArray[index]))
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
                if (!EqualsValue(property.Value, other))
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
    // objects (sorted keys, then values). NaN sorts as null but never
    // equals anything, matching the reference.
    internal static int Compare(JsonNode? l, JsonNode? r)
    {
        int leftRank = ValueRank(l);
        int rightRank = ValueRank(r);
        if (leftRank != rightRank)
            return leftRank.CompareTo(rightRank);
        if (l is JsonArray leftArray && r is JsonArray rightArray)
        {
            int shared = Math.Min(leftArray.Count, rightArray.Count);
            for (var index = 0; index < shared; index++)
            {
                int order = Compare(leftArray[index], rightArray[index]);
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
                int order = Compare(leftObject[key], rightObject[key]);
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

    internal static int Length(JsonNode? node) => node switch
    {
        null => 0,
        JsonArray arr => arr.Count,
        JsonObject obj => obj.Count,
        JsonValue v when v.TryGetValue<string>(out var s) => s.EnumerateRunes().Count(),
        _ => 0
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
        if (input is not JsonArray arr)
            throw new JqRuntimeException($"cannot iterate over {TypeName(input)}");
        var result = new JsonArray();
        budget.ChargeNode();
        foreach (JsonNode? child in arr)
            FlattenInto(result, child, depth);
        return result;
    }

    private void FlattenInto(JsonArray result, JsonNode? node, double depth)
    {
        if (node is JsonArray nested && depth != 0)
        {
            foreach (JsonNode? child in nested)
                FlattenInto(result, child, depth - 1);
            return;
        }
        budget.ChargeNode();
        result.Add(Clone(node));
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

    private bool ContainsSameKind(JsonNode? container, JsonNode? contained)
    {
        if (container is JsonObject obj && contained is JsonObject needle)
            return needle.All(kv => obj.TryGetPropertyValue(kv.Key, out var value) && ContainsSameKind(value, kv.Value));
        if (container is JsonArray haystack && contained is JsonArray needles)
            return needles.All(needle => haystack.Any(candidate => ContainsSameKind(candidate, needle)));
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
            // Scalar offsets, matching rune-wise substring search.
            var runes = new List<System.Text.Rune>();
            foreach (var rune in text.EnumerateRunes())
                runes.Add(rune);
            var wanted = new List<System.Text.Rune>();
            if (fragment is not null)
                foreach (var rune in fragment.EnumerateRunes())
                    wanted.Add(rune);
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
                start += Math.Max(0, wanted.Count - 1);
            }
            yield break;
        }
        if (input is JsonArray values)
        {
            if (needle is JsonArray pattern)
            {
                // Contiguous subsequence search; empty needles match everywhere.
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

    internal static string TrimString(JsonNode? input, JsonNode? trim, bool left, bool right)
    {
        var value = String(input);
        var t = String(trim);
        if (left && value.StartsWith(t, StringComparison.Ordinal)) value = value[t.Length..];
        if (right && value.EndsWith(t, StringComparison.Ordinal)) value = value[..^t.Length];
        return value;
    }

    internal JsonNode Explode(JsonNode? input)
    {
        var arr = new JsonArray();
        foreach (var rune in String(input).EnumerateRunes())
        {
            budget.ChargeNode();
            arr.Add(rune.Value);
        }
        return arr;
    }

    internal JsonNode Implode(JsonNode? input)
    {
        if (input is not JsonArray arr) throw new JqRuntimeException("implode expects an array");
        var sb = new StringBuilder();
        foreach (var item in arr) budget.Append(sb, char.ConvertFromUtf32((int)Number(item)));
        return JsonValue.Create(budget.Finish(sb));
    }

    internal JsonNode Split(JsonNode? input, JsonNode? separator)
    {
        var arr = new JsonArray();
        var text = String(input);
        var delimiter = String(separator);
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
        if (input is not JsonArray arr) throw new JqRuntimeException("join expects an array");
        var delimiter = String(separator);
        var builder = new StringBuilder();
        for (var i = 0; i < arr.Count; i++)
        {
            if (i > 0) budget.Append(builder, delimiter);
            budget.Append(builder, ToJqString(arr[i]));
        }
        return JsonValue.Create(budget.Finish(builder));
    }

    internal IEnumerable<JsonNode?> Range(IReadOnlyList<List<JsonNode?>> args)
    {
        if (args.Count is < 1 or > 3)
            throw new JqException("range expects one to three arguments");
        if (args.Any(arg => arg.Count == 0))
            yield break;

        var start = args.Count == 1 ? 0 : (int)Number(args[0][0]);
        var end = (int)Number(args.Count == 1 ? args[0][0] : args[1][0]);
        var step = args.Count > 2 ? (int)Number(args[2][0]) : 1;
        if (step == 0)
            throw new JqException("range step cannot be zero");

        for (long i = start; step > 0 ? i < end : i > end; i += step)
        {
            budget.ChargeNode();
            yield return JsonValue.Create((int)i);
        }
    }

    internal static bool AnyAll(JsonNode? input, bool any)
    {
        if (input is not JsonArray arr) throw new JqException(any ? "any expects an array" : "all expects an array");
        return any ? arr.Any(Truthy) : arr.All(Truthy);
    }

    internal JsonNode Strptime(JsonNode? input, JsonNode? format)
    {
        var dt = DateTimeOffset.ParseExact(String(input), ConvertDateFormat(String(format)), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return new JsonArray(dt.Year, dt.Month - 1, dt.Day, dt.Hour, dt.Minute, dt.Second, (int)dt.DayOfWeek, dt.DayOfYear - 1);
    }

    internal static DateTimeOffset UnixDate(JsonNode? input) => DateTimeOffset.FromUnixTimeSeconds((long)Number(input)).ToUniversalTime();

    internal string ConvertDateFormat(string format)
    {
        if (format.Length > 4096)
            throw new JqException("date format exceeds the 4096-character limit");
        budget.ChargeBytes(32L * format.Length);
        return format
            .Replace("%Y", "yyyy", StringComparison.Ordinal)
            .Replace("%m", "MM", StringComparison.Ordinal)
            .Replace("%d", "dd", StringComparison.Ordinal)
            .Replace("%H", "HH", StringComparison.Ordinal)
            .Replace("%M", "mm", StringComparison.Ordinal)
            .Replace("%S", "ss", StringComparison.Ordinal);
    }

    internal string Format(string format, JsonNode? value)
    {
        if (format == "json") return Serialize(value, false, null, false);
        if (format == "csv") return Delimited(value, ",");
        if (format == "tsv") return Tsv(value);
        var text = ToJqString(value);
        switch (format)
        {
            case "text": return text;
            case "html": return EncodeChunks(System.Net.WebUtility.HtmlEncode);
            case "uri": return EncodeChunks(Uri.EscapeDataString);
            case "sh":
            {
                var builder = new StringBuilder();
                budget.Append(builder, "'");
                foreach (var ch in text)
                    budget.Append(builder, ch == '\'' ? "'\\''" : new ReadOnlySpan<char>(in ch));
                budget.Append(builder, "'");
                return budget.Finish(builder);
            }
            case "base64":
            {
                var bytes = Encoding.UTF8.GetByteCount(text);
                budget.ChargeString(4 * ((bytes + 2L) / 3));
                budget.ChargeBytes(bytes);
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            }
            case "base64d":
            {
                budget.ChargeBytes(3L * text.Length / 4);
                var bytes = Convert.FromBase64String(text);
                budget.ChargeString(Encoding.UTF8.GetCharCount(bytes));
                return Encoding.UTF8.GetString(bytes);
            }
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

        string Tsv(JsonNode? value)
        {
            if (value is not JsonArray array)
                throw new JqException("@tsv requires an array");
            var builder = new StringBuilder();
            for (var i = 0; i < array.Count; i++)
            {
                if (i > 0) budget.Append(builder, "\t");
                var field = array[i];
                if (field is null) continue;
                if (field is JsonArray or JsonObject)
                    throw new JqException($"@tsv cannot format {TypeName(field)} as a field");

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

        string Delimited(JsonNode? value, string separator)
        {
            if (value is not JsonArray arr) return ToJqString(value);
            var builder = new StringBuilder();
            for (var i = 0; i < arr.Count; i++)
            {
                if (i > 0) budget.Append(builder, separator);
                var text = ToJqString(arr[i]);
                if (!text.Contains('"') && !text.Contains('\n') && !text.Contains(separator, StringComparison.Ordinal))
                    budget.Append(builder, text);
                else
                {
                    budget.Append(builder, "\"");
                    foreach (var ch in text)
                        budget.Append(builder, ch == '"' ? "\"\"" : new ReadOnlySpan<char>(in ch));
                    budget.Append(builder, "\"");
                }
            }
            return budget.Finish(builder);
        }
    }
}
