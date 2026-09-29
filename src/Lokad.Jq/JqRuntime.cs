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
            if (node == null) writer.WriteNullValue();
            else node.WriteTo(writer, ascii ? AsciiJson : CompactJson);
            writer.Flush();
        }
        budget.ChargeString(Encoding.UTF8.GetCharCount(buffer.WrittenMemory.Span));
        return buffer.WrittenMemory;
    }

    internal JsonNode? Clone(JsonNode? node)
    {
        budget.ChargeTree(node);
        return node?.DeepClone();
    }

    // Scan and charge tokens before allocating a DOM, including when reading a sequence of values.
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
            if (!reader.Read()) throw new JqException("expected a JSON value");
            var start = (int)reader.TokenStartIndex;
            do
            {
                budget.ChargeNode();
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                    budget.ChargeString(DecodedStringLength(reader.ValueSpan));
                if (reader.CurrentDepth == 0 && reader.TokenType is not (JsonTokenType.StartArray or JsonTokenType.StartObject))
                    break;
            } while (reader.Read());
            consumed = (int)reader.BytesConsumed;
            budget.ChargeBytes(consumed - start);
            return JsonNode.Parse(text[start..consumed], documentOptions: new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                AllowTrailingCommas = true,
                MaxDepth = JqBudget.MaximumDepth
            });
        }
        // Duplicate-property validation decodes names and can reject invalid UTF-16 escapes.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new JqException(ex.Message);
        }

        static int DecodedStringLength(ReadOnlySpan<byte> value)
        {
            var length = Encoding.UTF8.GetCharCount(value);
            // Utf8JsonReader has validated the escapes. Each represents one UTF-16 code unit;
            // a surrogate pair uses two Unicode escapes and therefore counts as two units.
            while (true)
            {
                var escape = value.IndexOf((byte)'\\');
                if (escape < 0) return length;
                var encodedLength = value[escape + 1] == (byte)'u' ? 6 : 2;
                length -= encodedLength - 1;
                value = value[(escape + encodedLength)..];
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

    internal JsonNode? Add(JsonNode? l, JsonNode? r)
    {
        if (l == null) return Clone(r);
        if (r == null) return Clone(l);
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
        return JsonValue.Create(Number(l) + Number(r));
    }

    internal JsonNode Multiply(JsonNode? l, JsonNode? r)
    {
        if (TryGetString(l, out var s) && TryGetInt(r, out var n))
        {
            var length = (long)s.Length * Math.Max(0, n);
            budget.ChargeString(length);
            if (length == 0)
                return JsonValue.Create(string.Empty);
            if (n == 1)
                return JsonValue.Create(s);
            if (s.Length == 1)
                return JsonValue.Create(new string(s[0], n));

            return JsonValue.Create(string.Create((int)length, s, static (destination, value) =>
            {
                for (var offset = 0; offset < destination.Length; offset += value.Length)
                    value.AsSpan().CopyTo(destination[offset..]);
            }));
        }
        return JsonValue.Create(Number(l) * Number(r));
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
        if (node is JsonValue n && n.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        if (node is JsonValue i && i.TryGetValue<int>(out var intValue)) return intValue.ToString(CultureInfo.InvariantCulture);
        if (node is JsonValue l && l.TryGetValue<long>(out var longValue)) return longValue.ToString(CultureInfo.InvariantCulture);
        if (node is JsonValue m && m.TryGetValue<decimal>(out var decimalValue)) return decimalValue.ToString(CultureInfo.InvariantCulture);
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

    internal static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is JsonValue v && v.TryGetValue(out value)) return true;
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
        return JsonNode.DeepEquals(l, r);
    }

    internal static int Compare(JsonNode? l, JsonNode? r)
    {
        if (TryGetString(l, out var ls) && TryGetString(r, out var rs)) return string.CompareOrdinal(ls, rs);
        return Number(l).CompareTo(Number(r));
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

    internal JsonNode? AddAll(JsonNode? input)
    {
        if (input is not JsonArray arr || arr.Count == 0) return null;
        JsonNode? result = null;
        foreach (var item in arr) result = Add(result, item);
        return result;
    }

    internal JsonNode Flatten(JsonNode? input)
    {
        var arr = new JsonArray();
        void AddItems(JsonNode? node)
        {
            if (node is JsonArray nested)
                foreach (var child in nested) AddItems(child);
            else
                arr.Add(Clone(node));
        }
        AddItems(input);
        return arr;
    }

    internal JsonNode? MinMax(JsonNode? input, bool max)
    {
        if (input is not JsonArray arr || arr.Count == 0) return null;
        var best = arr[0];
        foreach (var item in arr.Skip(1))
        {
            if ((max && Compare(item, best) > 0) || (!max && Compare(item, best) < 0))
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
        if (TryGetString(container, out var s) && TryGetString(contained, out var sub)) return s.Contains(sub, StringComparison.Ordinal);
        if (container is JsonArray arr) return arr.Any(v => JsonEquals(v, contained));
        if (container is JsonObject obj && contained is JsonObject needle)
            return needle.All(kv => obj.TryGetPropertyValue(kv.Key, out var value) && Contains(value, kv.Value));
        return JsonEquals(container, contained);
    }

    internal JsonNode Indices(JsonNode? input, JsonNode? needle)
    {
        var arr = new JsonArray();
        foreach (var index in MatchingIndices(input, needle)) arr.Add(index);
        return arr;
    }

    internal JsonNode? Index(JsonNode? input, JsonNode? needle)
    {
        foreach (var index in MatchingIndices(input, needle)) return JsonValue.Create(index);
        return null;
    }

    private IEnumerable<int> MatchingIndices(JsonNode? input, JsonNode? needle)
    {
        if (TryGetString(input, out var s) && TryGetString(needle, out var n))
        {
            var at = 0;
            while (at <= s.Length && (at = s.IndexOf(n, at, StringComparison.Ordinal)) >= 0)
            {
                budget.ChargeNode();
                yield return at;
                at += Math.Max(1, n.Length);
            }
            yield break;
        }
        if (input is JsonArray values)
        {
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
        var start = 0;
        while (true)
        {
            var end = delimiter.Length == 0 ? -1 : text.IndexOf(delimiter, start, StringComparison.Ordinal);
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
