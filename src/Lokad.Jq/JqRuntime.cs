using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal sealed partial class JqRuntime(JqBudget budget)
{
    private const int DiagnosticPreviewBytes = 30;
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions AsciiJson = new() { Encoder = JavaScriptEncoder.Default };

    internal string Serialize(JsonNode? node, bool ascii, int? indent, bool tabs)
    {
        using var rendered = SerializeUtf8(node, ascii, indent, tabs);
        budget.ReserveBytes(2L * Encoding.UTF8.GetCharCount(rendered.Value.Span));
        return Encoding.UTF8.GetString(rendered.Value.Span);
    }

    internal JqMemoryValue<ReadOnlyMemory<byte>> SerializeUtf8(JsonNode? node, bool ascii, int? indent, bool tabs) =>
        SerializeUtf8(node, ascii, indent, tabs, false);

    internal JqMemoryValue<ReadOnlyMemory<byte>> SerializeUtf8(JsonNode? node, bool ascii, int? indent, bool tabs, bool sorted) =>
        SerializeUtf8(node, ascii, indent, tabs, sorted, false, false);

    internal JqMemoryValue<ReadOnlyMemory<byte>> SerializeDebug(JsonNode? value, bool ascii, bool sorted) =>
        SerializeUtf8(value, ascii, null, false, sorted, false, true);

    private JqMemoryValue<ReadOnlyMemory<byte>> SerializeUtf8(JsonNode? node, bool ascii, int? indent, bool tabs, bool sorted, bool preview, bool debug)
    {
        using var keyOrder = sorted ? new JqComparer(budget) : null;
        JqJsonBuffer buffer = budget.RentSerializationBuffer();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, CreateWriterOptions(ascii, indent, tabs)))
            {
                try { WriteJson(node, ascii, writer, keyOrder, preview, debug); }
                finally { writer.Reset(); }
            }
            buffer.LowercaseUnicodeEscapes();
            if (debug)
            {
                buffer.GetSpan(1)[0] = (byte)'\n';
                buffer.Advance(1);
            }
            return new JqMemoryValue<ReadOnlyMemory<byte>>(buffer.WrittenMemory, budget.OwnSerializationBuffer(buffer));
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static JsonWriterOptions CreateWriterOptions(bool ascii, int? indent, bool tabs) => new()
        {
            Encoder = ascii ? JavaScriptEncoder.Default : JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = indent != null || tabs,
            IndentCharacter = tabs ? '\t' : ' ',
            IndentSize = tabs ? 1 : indent ?? 2,
            MaxDepth = JqRecursion.MaximumDepth,
            NewLine = "\n"
        };

    private void WriteJson(JsonNode? node, bool ascii, Utf8JsonWriter writer, JqComparer? keyOrder, bool preview, bool debug)
    {
        if (debug)
        {
            writer.WriteStartArray();
            writer.WriteStringValue("DEBUG:");
        }
        WriteValue(node, debug ? 1 : 0, budget, ascii, writer, keyOrder, preview);
        if (debug) writer.WriteEndArray();
        writer.Flush();
        budget.CheckCancellation();

        static bool WriteValue(JsonNode? value, int depth, JqBudget budget, bool ascii,
            Utf8JsonWriter writer, JqComparer? keyOrder, bool preview)
        {
            budget.CheckCancellation();
            JqRecursion.CheckValueDepth(value, depth);
            if (value is JsonArray array)
            {
                writer.WriteStartArray();
                if (Full()) return false;
                foreach (JsonNode? child in array)
                    if (!WriteValue(child, depth + 1, budget, ascii, writer, keyOrder, preview)) return false;
                writer.WriteEndArray();
            }
            else if (value is JsonObject obj)
            {
                writer.WriteStartObject();
                if (Full()) return false;
                IEnumerable<KeyValuePair<string, JsonNode?>> properties = keyOrder is not null && obj.Count > 1
                    ? keyOrder.OrderProperties(obj, depth) : obj;
                foreach (var property in properties)
                {
                    budget.CheckCancellation();
                    ReadOnlySpan<char> key = Prefix(property.Key);
                    writer.WritePropertyName(key);
                    if (key.Length != property.Key.Length || Full()) return false;
                    if (!WriteValue(property.Value, depth + 1, budget, ascii, writer, keyOrder, preview)) return false;
                }
                writer.WriteEndObject();
            }
            else if (value is null)
            {
                writer.WriteNullValue();
            }
            else if (value is JsonValue scalar && scalar.TryGetValue<double>(out double number) && !double.IsFinite(number))
            {
                if (double.IsNaN(number)) writer.WriteNullValue();
                else writer.WriteNumberValue(number > 0 ? double.MaxValue : double.MinValue);
            }
            else if (preview && TryGetString(value, out string? text) && text is not null)
                writer.WriteStringValue(Prefix(text));
            else value.WriteTo(writer, ascii ? AsciiJson : CompactJson);
            return !Full();

            bool Full() => preview && writer.BytesCommitted + writer.BytesPending >= DiagnosticPreviewBytes;

            ReadOnlySpan<char> Prefix(string text)
            {
                int length = preview ? Math.Min(DiagnosticPreviewBytes, text.Length) : text.Length;
                if (length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
                return text.AsSpan(0, length);
            }
        }
    }

    internal JsonNode? Clone(JsonNode? node)
    {
        return budget.CopyValue(node, 0);
    }

    internal JsonNode? Clone(JsonNode? node, int depth) => budget.CopyValue(node, depth);

    // Embeds a possibly-shared node into a fresh container. Pass-through
    // evaluation can now yield live references (identity shares its input
    // pointer like upstream DUP), and System.Text.Json forbids multiple
    // parents, so nodes that are already parented elsewhere are detached
    // before attaching them to another parent.
    internal JsonNode? Detach(JsonNode? node)
    {
        if (node is null || node.Parent is null)
            return node;
        return budget.CopyValue(node, 0);
    }

    // Single-pass decode: builds values while charging, so duplicate object
    // keys resolve last-wins at first position like object assignment.
    internal JsonNode? ReadJsonValue(ReadOnlySpan<byte> text, out int consumed) =>
        ReadJsonValue(text, 0, out consumed);

    internal JsonArray ReadJsonArray(ReadOnlySpan<byte> bytes, int childDepth)
    {
        budget.ReserveBytes(64);
        var values = new JsonArray();
        int offset = HasBomPrefix(bytes) ? 3 : 0;
        while (offset < bytes.Length)
        {
            budget.CheckCancellation();
            if (bytes[offset..].TrimStart(" \t\r\n"u8).IsEmpty) break;
            JsonNode? value = ReadJsonValue(bytes[offset..], childDepth, out int consumed);
            offset += consumed;
            // Parsing already owns the node; only the containing slot is new.
            budget.ReserveBytes(24);
            values.Add(value);
        }
        return values;
    }

    internal JsonNode? ReadJsonValue(ReadOnlySpan<byte> text, int depth, out int consumed)
    {
        try
        {
            var reader = new Utf8JsonReader(text, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                AllowMultipleValues = true,
                // Allow the next opening token so ReadValue rejects it as a
                // terminal quota before allocating, rather than a reader syntax error.
                MaxDepth = JqRecursion.MaximumDepth + 1
            });
            int firstContent = SkipJsonWhitespace(text, 0);
            if (TryMatchNonFinite(text, firstContent, out double topValue, out int topEnd))
            {
                budget.ReserveValueNode();
                consumed = topEnd;
                budget.CheckCancellation();
                return JsonValue.Create(topValue);
            }
            if (TryInvalidNonFiniteLiteral(text, firstContent, default, out string invalidMessage))
                throw new JqException(invalidMessage);
            if (!reader.Read())
                throw new JqException("expected a JSON value");
            int consumedBase = 0;
            JsonNode? value = ReadValue(ref reader, text, ref consumedBase, depth);
            consumed = consumedBase + (int)reader.BytesConsumed;
            budget.CheckCancellation();
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new JqException(ex.Message, ex);
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

    private static bool IsValuePreceded(ReadOnlySpan<byte> text, int position, InputPrefix prefix)
    {
        int index = position - 1;
        while (index >= 0 && text[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            index--;
        if (index < 0)
            return prefix.LastNonWhitespace is 0 or (byte)'[' or (byte)',' or (byte)':';
        return text[index] is (byte)'[' or (byte)',' or (byte)':';
    }

    private bool TryInvalidNonFiniteLiteral(ReadOnlySpan<byte> text, int position, InputPrefix prefix, out string message)
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
        prefix.Advance(text, budget);
        message = "Invalid numeric literal at EOF at line " + (prefix.LineBreaks + 1) + ", column " + prefix.Column;
        return true;
    }

    private static bool IsLiteralByte(byte value) =>
        value != (byte)' ' && value != (byte)'\t' && value != (byte)'\r' && value != (byte)'\n' &&
        value != (byte)'[' && value != (byte)',' && value != (byte)']' && value != (byte)'{' &&
        value != (byte)':' && value != (byte)'}' && value != (byte)'"';

    // The incremental reader only retains incomplete token bytes. This small
    // prefix summary preserves extended-literal validation and EOF positions.
    private struct InputPrefix
    {
        internal byte LastNonWhitespace;
        internal int LineBreaks;
        internal int Column;

        internal void Advance(ReadOnlySpan<byte> text, JqBudget budget)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if ((i & 4095) == 0) budget.CheckCancellation();
                byte current = text[i];
                if (current == (byte)'\n')
                {
                    LineBreaks++;
                    Column = 0;
                }
                else Column++;
                if (current is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                    LastNonWhitespace = current;
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

    // A restored state expecting a value needs the following comma consumed
    // here. A post-value state consumes it itself. Resumed values preserve
    // that state kind, including across incremental host reads.
    private void ReadToken(ref Utf8JsonReader reader, ReadOnlySpan<byte> source, ref int consumedBase, InputPrefix prefix, JsonReaderState restoreState, bool consumeComma, out bool hasToken, out JsonNode? nonFinite)
        {
            nonFinite = null;
            int checkpoint = consumedBase + (int)reader.BytesConsumed;
            JsonReaderState checkpointState = reader.CurrentState;
            try
            {
                hasToken = reader.Read();
            }
            catch (JsonException)
            {
                int failure = consumedBase + (int)reader.BytesConsumed;
                if (!reader.IsFinalBlock && IsValuePreceded(source, failure, prefix)
                    && IsIncompleteNonFinite(source, failure))
                {
                    reader = new Utf8JsonReader(source[checkpoint..], false, checkpointState);
                    consumedBase = checkpoint;
                    hasToken = false;
                    return;
                }
                if (IsValuePreceded(source, failure, prefix)
                    && TryInvalidNonFiniteLiteral(source, failure, prefix, out string nestedInvalid))
                    throw new JqException(nestedInvalid);
                if (IsValuePreceded(source, failure, prefix)
                    && TryMatchNonFinite(source, failure, out double number, out int end))
                {
                    int resume = SkipJsonWhitespace(source, end);
                    // A token or separator may continue in the next host read.
                    if (!reader.IsFinalBlock && (end == source.Length || consumeComma && resume == source.Length))
                    {
                        reader = new Utf8JsonReader(source[checkpoint..], false, checkpointState);
                        consumedBase = checkpoint;
                        hasToken = false;
                        return;
                    }
                    budget.ReserveValueNode();
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

            static bool IsIncompleteNonFinite(ReadOnlySpan<byte> source, int position)
            {
                int index = SkipJsonWhitespace(source, position);
                if (index < source.Length && source[index] is (byte)'+' or (byte)'-') index++;
                ReadOnlySpan<byte> word = source[index..];
                return IsPrefix(word, "nan"u8) || IsPrefix(word, "infinity"u8);

                static bool IsPrefix(ReadOnlySpan<byte> word, ReadOnlySpan<byte> literal)
                {
                    if (word.Length > literal.Length) return false;
                    for (int i = 0; i < word.Length; i++)
                    {
                        byte candidate = word[i];
                        if (candidate is >= (byte)'A' and <= (byte)'Z') candidate += 32;
                        if (candidate != literal[i]) return false;
                    }
                    return true;
                }
            }
        }

        JsonNode? ReadValue(ref Utf8JsonReader reader, ReadOnlySpan<byte> source, ref int consumedBase, int depth)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    JqRecursion.CheckDepth(depth + 1, "value nesting limit exceeded");
                    budget.ReserveValueNode();
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
                        string name = ReadAdmittedString(ref reader);
                        budget.ReserveBytes(96);
                        ReadToken(ref reader, source, ref consumedBase, default, headState, expectsObjectValue, out bool hasValue, out JsonNode? valueFallback);
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
                    JqRecursion.CheckDepth(depth + 1, "value nesting limit exceeded");
                    budget.ReserveValueNode();
                    var array = new JsonArray();
                    bool expectsValue = true;
                    while (true)
                    {
                        JsonReaderState headState = reader.CurrentState;
                        ReadToken(ref reader, source, ref consumedBase, default, headState, expectsValue, out bool hasToken, out JsonNode? nonFinite);
                        if (!hasToken)
                            throw new JqException("truncated JSON value");
                        if (nonFinite is not null)
                        {
                            budget.ReserveBytes(24);
                            array.Add(nonFinite);
                            continue;
                        }
                        if (reader.TokenType == JsonTokenType.EndArray)
                            return array;
                        budget.ReserveBytes(24);
                        array.Add(ReadValue(ref reader, source, ref consumedBase, depth + 1));
                        expectsValue = false;
                    }
                default:
                    return ReadScalar(ref reader);
            }
        }

    private JsonNode? ReadScalar(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number
            or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null))
            throw new JqException("expected a JSON value");
        if (reader.TokenType == JsonTokenType.Null) budget.CheckCancellation();
        else budget.ReserveValueNode();
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return JsonValue.Create(ReadAdmittedString(ref reader));
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long whole))
                {
                    if (whole == 0 && reader.ValueSpan.Length > 0 && reader.ValueSpan[0] == (byte)'-')
                        return JsonValue.Create(reader.GetDouble());
                    return JsonValue.Create(whole);
                }
                return JsonValue.Create(reader.GetDouble());
            case JsonTokenType.True:
                return JsonValue.Create(true);
            case JsonTokenType.False:
                return JsonValue.Create(false);
            case JsonTokenType.Null:
                return null;
            default:
                throw new JqException("expected a JSON value");
        }
    }

    private string ReadAdmittedString(ref Utf8JsonReader reader)
    {
        // The reader validates the token's escape syntax. Count decoded UTF-16
        // units without creating the string, then preflight its allocation.
        ReadOnlySpan<byte> remaining = reader.ValueSpan;
        int length = 0;
        if (reader.ValueIsEscaped)
        {
            int escape;
            while ((escape = remaining.IndexOf((byte)'\\')) >= 0)
            {
                length += Encoding.UTF8.GetCharCount(remaining[..escape]) + 1;
                budget.CheckCancellation();
                // Each escape contributes one code unit, including each half
                // of a surrogate pair encoded with two Unicode escapes.
                int width = remaining[escape + 1] == (byte)'u' ? 6 : 2;
                remaining = remaining[(escape + width)..];
            }
        }
        length += Encoding.UTF8.GetCharCount(remaining);
        budget.ReserveString(length);
        try { return reader.GetString() ?? throw new JqException("expected a JSON string"); }
        catch (InvalidOperationException exception) { throw new JqException(exception.Message, exception); }
    }

    // Like the reference fromjson, only strings parse; other inputs fail
    // with the upstream diagnostic before any decoding.
    internal JsonNode? ParseJson(JsonNode? input)
    {
        if (!TryGetString(input, out string? text) || text is null)
            throw new JqException($"{TypeName(input)} ({TruncatedDump(input)}) only strings can be parsed");
        return ParseJson(text);
    }

    internal JsonNode? ParseJson(string text)
    {
        budget.CheckCancellation();
        int capacity = JqBudget.BufferCapacity(Utf8Text.GetByteCount(text, budget.CancellationToken), 0);
        using var memory = new JqMemoryReservation(budget.MemoryBudget);
        if (capacity != 0) memory.Resize(capacity);
        budget.CheckCancellation();
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
    // strings render quoted, truncated like jv_dump_string_trunc with a
    // 30-byte buffer).
    internal string TypeError(JsonNode? l, JsonNode? r, string verb) =>
        $"{TypeName(l)} ({TruncatedDump(l)}) and {TypeName(r)} ({TruncatedDump(r)}) {verb}";

    // Reference-shaped operand dump for unary type errors (negation and its
    // parser/math aliases share the message).
    internal string UnaryTypeError(JsonNode? value, string verb) =>
        $"{TypeName(value)} ({TruncatedDump(value)}) {verb}";

    // Mirrors jv_dump_string_trunc with a 30-byte buffer: dumps longer than
    // 29 bytes keep 25 bytes (26 without a closing delimiter for scalars
    // other than strings) backed over the UTF-8 boundary, then take "..."
    // with the closing delimiter. The shared writer stops after obtaining
    // that prefix, including when a string or property name is much longer.
    internal string TruncatedDump(JsonNode? node)
    {
        using var rendered = SerializeUtf8(node, false, null, false, false, true, false);
        ReadOnlySpan<byte> dumped = rendered.Value.Span;
        if (dumped.Length < DiagnosticPreviewBytes)
        {
            budget.ReserveString(Encoding.UTF8.GetCharCount(dumped));
            return Encoding.UTF8.GetString(dumped);
        }
        char delimiter = dumped[0] switch
        {
            (byte)'"' => '"',
            (byte)'[' => ']',
            (byte)'{' => '}',
            _ => '\0',
        };
        int keep = DiagnosticPreviewBytes - (delimiter == '\0' ? 4 : 5);
        while (keep > 0 && dumped[keep] >= 0x80 && dumped[keep] < 0xC0)
            keep--;
        ReadOnlySpan<byte> prefix = dumped[..keep];
        budget.ReserveString(Encoding.UTF8.GetCharCount(prefix) + (delimiter == '\0' ? 3 : 4));
        string head = Encoding.UTF8.GetString(prefix);
        return delimiter == '\0' ? head + "..." : head + "..." + delimiter;
    }

    internal JsonNode? Add(JsonNode? l, JsonNode? r)
    {
        budget.CheckCancellation();
        if (l == null) return r;
        if (r == null) return l;
        if (TypeName(l) == "number" && TypeName(r) == "number")
            return JsonValue.Create(Number(l) + Number(r));
        if (TryGetString(l, out var ls) && TryGetString(r, out var rs))
        {
            budget.ReserveString((long)ls.Length + rs.Length);
            return JsonValue.Create(ls + rs);
        }
        if (l is JsonArray la && r is JsonArray ra)
        {
            if (ra.Count == 0) return la;
            var arr = new JsonArray();
            foreach (var value in la.Concat(ra))
            {
                budget.ReserveArrayItem(value);
                arr.Add(budget.CopyValue(value, 1));
            }
            return arr;
        }
        if (l is JsonObject lo && r is JsonObject ro)
            return MergeObjects(lo, ro, false, 0);
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
                {
                    budget.ReserveArrayItem(item);
                    result.Add(budget.CopyValue(item, 1));
                }
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
            return MergeObjects(lo, ro, true, 0);
        throw new JqRuntimeException(TypeError(l, r, "cannot be multiplied"));
    }

    private JsonNode? Repeat(string value, double count)
    {
        // Truncation matches the reference repeat mapping; negative and NaN
        // counts produce null.
        int times = double.IsNaN(count) || count < 0 ? -1 : count > int.MaxValue ? int.MaxValue : (int)count;
        if (times < 0)
            return null;
        budget.CheckCancellation();
        var length = (long)value.Length * times;
        if (length == 0)
            return JsonValue.Create(string.Empty);
        if (times == 1)
            return JsonValue.Create(value);
        budget.ReserveString(length);
        if (value.Length == 1)
            return JsonValue.Create(new string(value[0], times));

        return JsonValue.Create(string.Create((int)length, (value, budget), static (destination, state) =>
        {
            int iteration = 0;
            for (var offset = 0; offset < destination.Length; offset += state.value.Length)
            {
                if ((iteration++ & 1023) == 0) state.budget.CheckCancellation();
                state.value.AsSpan().CopyTo(destination[offset..]);
            }
        }));
    }

    private JsonObject MergeObjects(JsonObject left, JsonObject right, bool recursive, int depth)
    {
        budget.CheckCancellation();
        // Nested results need private nodes for their new parent; the root can borrow.
        if (depth == 0 && right.Count == 0) return left;
        JqRecursion.CheckValueDepth(left, depth);
        JqRecursion.CheckValueDepth(right, depth);
        budget.ReserveBytes(64 + 96L * left.Count);
        var merged = new JsonObject();
        foreach (var property in left)
        {
            budget.CheckCancellation();
            bool replaced = right.TryGetPropertyValue(property.Key, out JsonNode? incoming);
            if (recursive && property.Value is JsonObject existingObject && incoming is JsonObject incomingObject)
                merged.Add(property.Key, MergeObjects(existingObject, incomingObject, true, depth + 1));
            else
            {
                JsonNode? selected = replaced ? incoming : property.Value;
                if (selected is JsonValue) budget.ReserveBytes(64);
                merged.Add(property.Key, budget.CopyValue(selected, depth + 1));
            }
        }
        foreach (var property in right)
        {
            budget.CheckCancellation();
            if (left.ContainsKey(property.Key)) continue;
            budget.ReserveObjectItem(property.Value);
            merged.Add(property.Key, budget.CopyValue(property.Value, depth + 1));
        }
        return merged;
    }

    // Projects a number already checked by its caller; textual conversion is tonumber.
    internal static double Number(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        if (node is JsonValue i && i.TryGetValue<int>(out var intValue)) return intValue;
        if (node is JsonValue l && l.TryGetValue<long>(out var longValue)) return longValue;
        if (node is JsonValue m && m.TryGetValue<decimal>(out var decimalValue)) return (double)decimalValue;
        throw new JqRuntimeException($"expected number, got {TypeName(node)}");
    }

    internal JsonNode ToJsonNumber(JsonNode? node)
    {
        budget.CheckCancellation();
        if (node is JsonValue numberValue && TypeName(numberValue) == "number")
            return numberValue;
        if (TryGetString(node, out string? text) && text is not null)
        {
            if (TryParseNumberText(text, out JsonNode? number) && number is not null)
                return number;
            if (TryParseNonFiniteNumber(text, out JsonNode? nonFinite) && nonFinite is not null)
                return nonFinite;
        }
        throw new JqException($"{TypeName(node)} ({TruncatedDump(node)}) cannot be parsed as a number");
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

    // jq numeric text permits a leading plus, omitted whole digits and leading
    // zeros, but no surrounding whitespace. Nonzero integers keep exact storage.
    private bool TryParseNumberText(string text, out JsonNode? number)
    {
        number = null;
        int position = 0;
        if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            position++;
        int whole = position;
        ScanDigits();
        bool hasWhole = position > whole;
        bool hasFraction = false;
        if (position < text.Length && text[position] == '.')
        {
            position++;
            int fraction = position;
            ScanDigits();
            hasFraction = position > fraction;
            if (!hasFraction)
                return false;
        }
        if (!hasWhole && !hasFraction)
            return false;
        bool hasExponent = position < text.Length && (text[position] == 'e' || text[position] == 'E');
        if (hasExponent)
        {
            position++;
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
                position++;
            int exponent = position;
            ScanDigits();
            if (position == exponent)
                return false;
        }
        if (position != text.Length)
            return false;
        budget.CheckCancellation();
        if (!hasFraction && !hasExponent)
        {
            bool integral = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long wholeValue);
            budget.CheckCancellation();
            if (integral)
            {
                number = wholeValue == 0 && text[0] == '-' ? JsonValue.Create(-0.0) : JsonValue.Create(wholeValue);
                return true;
            }
        }
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
        budget.CheckCancellation();
        if (parsed)
        {
            number = JsonValue.Create(value);
            return true;
        }
        return false;

        void ScanDigits()
        {
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                if ((position & 4095) == 0) budget.CheckCancellation();
                position++;
            }
        }
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
        throw new JqException($"{TypeName(node)} ({TruncatedDump(node)}) cannot be parsed as a boolean");
    }

    internal static long Utf8ByteLength(JsonNode? input, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TryGetString(input, out string? text) && text is not null)
            return Utf8Text.GetByteCount(text, context.Budget.CancellationToken);
        throw new JqException($"{TypeName(input)} ({context.Runtime.TruncatedDump(input)}) only strings have UTF-8 byte length");
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
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s))
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

    internal static bool Truthy(JsonNode? node)
    {
        if (node == null) return false;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return true;
    }

    internal bool JsonEquals(JsonNode? l, JsonNode? r)
    {
        budget.CheckCancellation();
        return EqualsValue(l, r, 0);
    }

    // Mirrors the reference value equality: kind-sensitive, objects
    // order-insensitive, arrays ordered, numbers by double value with
    // Scalar NaN is unequal to itself; shared containers compare by identity.
    // Depth-bounded to protect recursive comparison: values deeper than the shared guard
    // fail cleanly instead of consuming CLR frames.
    private bool EqualsValue(JsonNode? left, JsonNode? right, int depth)
    {
        budget.CheckCancellation();
        if (left is JsonArray or JsonObject && ReferenceEquals(left, right)) return true;
        JqRecursion.CheckValueDepth(left, depth);
        JqRecursion.CheckValueDepth(right, depth);
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
    internal int Compare(JsonNode? left, JsonNode? right) => JqComparer.CompareValues(left, right, budget);

    private static bool IsNaNNumber(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number && double.IsNaN(Number(node));

    internal JsonNode? Length(JsonNode? node) => node switch
    {
        null => JsonValue.Create(0),
        JsonArray arr => JsonValue.Create(arr.Count),
        JsonObject obj => JsonValue.Create(obj.Count),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(CountStringScalars(s)),
        // Like the reference number branch, the length of a number is its
        // absolute value; anything else has no length.
        JsonValue v when TypeName(v) == "number" => JsonValue.Create(Math.Abs(Number(v))),
        _ => throw new JqException($"{TypeName(node)} ({TruncatedDump(node)}) has no length"),
    };

    internal int CountStringScalars(ReadOnlySpan<char> text)
    {
        budget.CheckCancellation();
        int count = 0;
        for (var runes = text.EnumerateRunes(); runes.MoveNext();)
        {
            if ((count & 4095) == 0) budget.CheckCancellation();
            count++;
        }
        budget.CheckCancellation();
        return count;
    }

    internal string SliceString(string text, double? start, double? end)
    {
        JqPaths.ResolveSlice(CountStringScalars(text), start, end, out int from, out int to);
        int first = 0;
        int last = 0;
        int scalar = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if ((scalar & 4095) == 0) budget.CheckCancellation();
            if (scalar == from) first = last;
            if (scalar++ == to) break;
            last += rune.Utf16SequenceLength;
        }
        if (from == to) first = last;
        budget.CheckCancellation();
        if (first == 0 && last == text.Length) return text;
        budget.ReserveString(last - first);
        return text[first..last];
    }

    internal static string TypeName(JsonNode? node) => (node?.GetValueKind() ?? JsonValueKind.Null) switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.String => "string",
        _ => "number"
    };

    internal JsonNode Keys(JsonNode? input, bool sorted)
    {
        if (input is JsonObject obj)
        {
            budget.ReserveBytes(64 + 88L * obj.Count);
            using var comparer = sorted ? new JqComparer(budget) : null;
            IEnumerable<KeyValuePair<string, JsonNode?>> properties = comparer is null ? obj : comparer.OrderProperties(obj, 0);
            var keys = new JsonArray();
            foreach (var property in properties)
            {
                budget.CheckCancellation();
                keys.Add(JsonValue.Create(property.Key));
            }
            return keys;
        }
        if (input is JsonArray arr)
        {
            budget.ReserveBytes(88L * arr.Count);
            var keys = new JsonArray();
            for (int index = 0; index < arr.Count; index++)
            {
                budget.CheckCancellation();
                keys.Add(JsonValue.Create(index));
            }
            return keys;
        }
        throw new JqException($"{TypeName(input)} ({TruncatedDump(input)}) has no keys");
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
                budget.CheckCancellation();
                budget.ReserveBytes(248);
                budget.ReserveObjectItem(property.Value);
                var entry = new JsonObject();
                entry["key"] = JsonValue.Create(property.Key);
                entry["value"] = budget.CopyValue(property.Value, 2);
                entries.Add(entry);
            }
            return entries;
        }
        if (input is JsonArray arr)
        {
            for (int index = 0; index < arr.Count; index++)
            {
                budget.CheckCancellation();
                budget.ReserveBytes(248);
                budget.ReserveObjectItem(arr[index]);
                var entry = new JsonObject();
                entry["key"] = JsonValue.Create(index);
                entry["value"] = budget.CopyValue(arr[index], 2);
                entries.Add(entry);
            }
            return entries;
        }
        throw new JqException($"{TypeName(input)} ({TruncatedDump(input)}) has no keys");
    }

    internal JsonNode FromEntries(JsonNode? input) =>
        FromEntryValues(IteratorFilter.ReadValues(input, false, budget));

    internal JsonObject FromEntryValues(IEnumerable<JsonNode?> entries)
    {
        var result = new JsonObject();
        foreach (JsonNode? element in entries)
        {
            budget.CheckCancellation();
            JsonNode? key = JqPathReads.ReadField(element, "key", this);
            if (!Truthy(key)) key = JqPathReads.ReadField(element, "Key", this);
            if (!Truthy(key)) key = JqPathReads.ReadField(element, "name", this);
            if (!Truthy(key)) key = JqPathReads.ReadField(element, "Name", this);
            if (key is null || !TryGetString(key, out string? keyName) || keyName is null)
                throw new JqException($"Cannot use {TypeName(key)} ({TruncatedDump(key)}) as object key");
            budget.CheckCancellation();
            JsonNode? value = element is JsonObject entry && entry.TryGetPropertyValue("value", out JsonNode? direct)
                ? direct : JqPathReads.ReadField(element, "Value", this);
            budget.ReserveObjectItem(value);
            result[keyName] = budget.CopyValue(value, 1);
        }
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<JsonNode?> AddValues(IAsyncEnumerable<JsonNode?> values)
    {
        JsonNode? result = null;
        bool ownsContainer = false;
        StringBuilder? text = null;
        await foreach (JsonNode? value in values.ConfigureAwait(false))
        {
            budget.CheckCancellation();
            if (text is not null)
            {
                if (value is null) continue;
                if (TryGetString(value, out string? piece))
                {
                    budget.Append(text, piece);
                    continue;
                }
                result = JsonValue.Create(budget.Finish(text));
                text = null;
            }
            if (ownsContainer && result is JsonArray accumulated && value is JsonArray incoming)
            {
                // This accumulator was constructed here and has never escaped.
                foreach (JsonNode? item in incoming)
                {
                    budget.ReserveArrayItem(item);
                    accumulated.Add(budget.CopyValue(item, 1));
                }
            }
            else if (ownsContainer && result is JsonObject merged && value is JsonObject properties)
            {
                foreach (var property in properties)
                {
                    budget.ReserveObjectItem(property.Value);
                    merged[property.Key] = budget.CopyValue(property.Value, 1);
                }
            }
            else if (TryGetString(result, out string? left) && TryGetString(value, out string? right))
            {
                text = new StringBuilder();
                budget.Append(text, left);
                budget.Append(text, right);
                result = null;
            }
            else
            {
                bool retainsOwned = ownsContainer && value is null;
                bool combinesContainers = result is JsonArray && value is JsonArray
                    || result is JsonObject && value is JsonObject;
                result = Add(result, value);
                ownsContainer = retainsOwned || combinesContainers;
            }
        }
        return text is null ? result : JsonValue.Create(budget.Finish(text));
    }

    internal JsonNode Flatten(JsonNode? input, JsonNode? depth)
    {
        budget.CheckCancellation();
        if (input is not JsonArray && input is not JsonObject)
            throw new JqRuntimeException($"cannot iterate over {TypeName(input)}");
        budget.ReserveBytes(64);
        var result = new JsonArray();
        if (input is JsonArray { Count: 0 } or JsonObject { Count: 0 }) return result;
        bool numeric = FlattenFilter.TryDepthLevel(depth, out double level);
        using var cursor = new JqTreeCursor(budget);
        cursor.Push(input);
        while (cursor.Count != 0)
        {
            if (!cursor.TryNext(out JsonNode? child))
            {
                cursor.Pop();
                continue;
            }
            if (child is JsonArray nested)
            {
                // Nonnumeric depths fail only when a nested array needs depth - 1.
                if (!numeric) _ = Subtract(depth, JsonValue.Create(1));
                if (level != cursor.Count - 1)
                {
                    if (nested.Count != 0) cursor.Push(nested);
                    continue;
                }
            }
            budget.ReserveArrayItem(child);
            result.Add(budget.CopyValue(child, 1));
        }
        return result;
    }

    internal JsonArray SortArray(JsonNode? input) => OrderedArray(input, false);

    internal JsonNode UniqueArray(JsonNode? input) => OrderedArray(input, true);

    private JsonArray OrderedArray(JsonNode? input, bool unique)
    {
        if (input is not JsonArray arr)
            throw new JqException("cannot be sorted, as it is not an array");
        budget.ReserveBytes(32L * arr.Count);
        var result = new JsonArray();
        budget.CheckCancellation();
        using var comparer = new JqComparer(budget);
        JsonNode? previous = null;
        bool first = true;
        foreach (JsonNode? element in JqComparer.OrderBy(arr, static element => element, (IComparer<JsonNode?>)comparer))
        {
            budget.CheckCancellation();
            if (unique && !first && JsonEquals(previous, element)) continue;
            budget.ReserveArrayItem(element);
            result.Add(budget.CopyValue(element, 1));
            previous = element;
            first = false;
        }
        return result;
    }

    internal JsonNode? MinMax(JsonNode? input, bool max)
    {
        budget.CheckCancellation();
        if (input is not JsonArray arr)
            throw new JqException("cannot be iterated over");
        if (arr.Count == 0) return null;
        var best = arr[0];
        using var comparer = new JqComparer(budget);
        for (int index = 1; index < arr.Count; index++)
        {
            JsonNode? item = arr[index];
            int order = comparer.Compare(item, best);
            if (max ? order >= 0 : order < 0)
                best = item;
        }
        return best;
    }

    internal JsonNode Reverse(JsonNode? input)
    {
        // Mirror the reference definition ([.[length-1-range(0;length)]]):
        // length-zero inputs yield [], arrays reverse natively, and anything
        // else fails staged (the reference raises index errors there).
        if (input is JsonArray arr)
        {
            var rev = new JsonArray();
            for (var i = arr.Count - 1; i >= 0; i--)
            {
                budget.ReserveArrayItem(arr[i]);
                rev.Add(budget.CopyValue(arr[i], 1));
            }
            return rev;
        }
        if (Number(Length(input)) == 0)
        {
            budget.CheckCancellation();
            return new JsonArray();
        }
        throw new JqRuntimeException($"cannot reverse {TypeName(input)}");
    }

    internal bool Contains(JsonNode? container, JsonNode? contained)
    {
        if (TypeName(container) != TypeName(contained))
            throw new JqException($"{TypeName(container)} ({TruncatedDump(container)}) and {TypeName(contained)} ({TruncatedDump(contained)}) cannot have their containment checked");
        return ContainsSameKind(container, contained);
    }

    private bool ContainsSameKind(JsonNode? container, JsonNode? contained) => ContainsSameKind(container, contained, 0);

    // Depth-bounded to protect recursive comparison: values deeper than the shared guard
    // fail cleanly instead of consuming CLR frames.
    private bool ContainsSameKind(JsonNode? container, JsonNode? contained, int depth)
    {
        budget.CheckCancellation();
        JqRecursion.CheckValueDepth(container, depth);
        JqRecursion.CheckValueDepth(contained, depth);
        if (container is JsonObject obj && contained is JsonObject needle)
        {
            for (int index = 0; index < needle.Count; index++)
            {
                budget.CheckCancellation();
                var property = needle.GetAt(index);
                if (!obj.TryGetPropertyValue(property.Key, out JsonNode? value)
                    || !ContainsSameKind(value, property.Value, depth + 1)) return false;
            }
            return true;
        }
        if (container is JsonArray haystack && contained is JsonArray needles)
        {
            for (int index = 0; index < needles.Count; index++)
            {
                budget.CheckCancellation();
                bool found = false;
                for (int candidate = 0; candidate < haystack.Count; candidate++)
                    if (ContainsSameKind(haystack[candidate], needles[index], depth + 1))
                    {
                        found = true;
                        break;
                    }
                if (!found) return false;
            }
            return true;
        }
        if (TryGetString(container, out var text) && TryGetString(contained, out var fragment))
            return fragment.Length == 0 || text.Contains(fragment, StringComparison.Ordinal);
        return JsonEquals(container, contained);
    }

    internal JsonNode Indices(JsonNode? input, JsonNode? needle)
    {
        var arr = new JsonArray();
        foreach (var index in MatchingIndices(input, needle))
        {
            budget.ReserveBytes(88);
            arr.Add(JsonValue.Create(index));
        }
        return arr;
    }

    internal JsonNode? Index(JsonNode? input, JsonNode? needle, bool last)
    {
        if (TryGetString(input, out string text) && TryGetString(needle, out string fragment))
        {
            int found = FindStringIndex(text, fragment, 0, last);
            return found < 0 ? null : JsonValue.Create(CountStringScalars(text.AsSpan(0, found)));
        }
        int? position = null;
        foreach (int index in MatchingIndices(input, needle))
        {
            if (!last) return JsonValue.Create(index);
            position = index;
        }
        return position is { } result ? JsonValue.Create(result) : null;
    }

    // Each ordinal call searches at most 64 Ki candidate starts. Its source
    // window also includes the whole needle; cancellation is checked around it.
    private int FindStringIndex(string text, string fragment, int start, bool last)
    {
        budget.CheckCancellation();
        if (fragment.Length == 0 || fragment.Length > text.Length) return -1;
        const int windowStarts = 65536;
        int lastStart = text.Length - fragment.Length;
        while (start <= lastStart)
        {
            budget.CheckCancellation();
            int count = Math.Min(windowStarts, lastStart - start + 1);
            int first = last ? lastStart - count + 1 : start;
            ReadOnlySpan<char> window = text.AsSpan(first, fragment.Length + (count - 1));
            int relative = last ? window.LastIndexOf(fragment.AsSpan()) : window.IndexOf(fragment.AsSpan());
            budget.CheckCancellation();
            if (relative < 0)
            {
                if (last) lastStart = first - 1;
                else start += count;
                continue;
            }
            int found = first + relative;
            // Rune-based callers never start a match inside a surrogate pair.
            if (!(found != 0 && char.IsLowSurrogate(text[found]) && char.IsHighSurrogate(text[found - 1])))
                return found;
            if (last) lastStart = found - 1;
            else start = found + 1;
        }
        return -1;
    }

    private IEnumerable<int> MatchingIndices(JsonNode? input, JsonNode? needle)
    {
        budget.CheckCancellation();
        if (TryGetString(input, out var text) && text is not null && TryGetString(needle, out var fragment))
        {
            int position = 0;
            int scalar = 0;
            while (true)
            {
                int found = FindStringIndex(text, fragment, position, false);
                if (found < 0) yield break;
                scalar += CountStringScalars(text.AsSpan(position, found - position));
                yield return scalar;
                Rune.DecodeFromUtf16(text.AsSpan(found), out _, out int width);
                position = found + width;
                scalar++;
            }
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
                    budget.CheckCancellation();
                    bool match = true;
                    for (int offset = 0; offset < pattern.Count; offset++)
                        if (!JsonEquals(values[start + offset], pattern[offset]))
                        {
                            match = false;
                            break;
                        }
                    if (!match)
                        continue;
                    budget.CheckCancellation();
                    yield return start;
                }
                yield break;
            }
            for (var i = 0; i < values.Count; i++)
                if (JsonEquals(values[i], needle))
                {
                    budget.CheckCancellation();
                    yield return i;
                }
            yield break;
        }
        throw new JqRuntimeException($"cannot search {TypeName(input)}");
    }

    internal string AsciiCase(JsonNode? input, bool lower)
    {
        budget.CheckCancellation();
        // Like the reference explode|map|implode definition, non-strings fail at the explode step.
        if (!TryGetString(input, out string? text) || text is null)
            throw new JqException("explode input must be a string");
        int first = 0;
        while (first < text.Length)
        {
            if ((first & 4095) == 0) budget.CheckCancellation();
            if (Map(text[first], lower) != text[first]) break;
            first++;
        }
        budget.CheckCancellation();
        if (first == text.Length) return text;
        budget.ReserveString(text.Length);
        budget.CheckCancellation();
        return string.Create(text.Length, (Text: text, Lower: lower, Budget: budget), static (destination, state) =>
        {
            for (int index = 0; index < state.Text.Length; index++)
            {
                if ((index & 4095) == 0) state.Budget.CheckCancellation();
                destination[index] = Map(state.Text[index], state.Lower);
            }
            state.Budget.CheckCancellation();
        });

        static char Map(char ch, bool lower) => lower && ch is >= 'A' and <= 'Z' ? (char)(ch + 32)
            : !lower && ch is >= 'a' and <= 'z' ? (char)(ch - 32) : ch;
    }

    internal static bool StartsEndsWith(JsonNode? input, JsonNode? affix, bool fromStart)
    {
        if (!TryGetString(input, out string? text) || text is null || !TryGetString(affix, out string? fix) || fix is null)
            throw new JqException((fromStart ? "startswith" : "endswith") + "() requires string inputs");
        return fromStart
            ? text.StartsWith(fix, StringComparison.Ordinal)
            : text.EndsWith(fix, StringComparison.Ordinal);
    }

    internal JsonNode TrimAffix(JsonNode? input, JsonNode? affix, bool left, bool right)
    {
        budget.CheckCancellation();
        if (input is not JsonValue scalar || !TryGetString(input, out string? text) || text is null || !TryGetString(affix, out string? fix) || fix is null)
            throw new JqException((left ? "startswith" : "endswith") + "() requires string inputs");
        if (fix.Length == 0) return scalar;
        if (left && text.StartsWith(fix, StringComparison.Ordinal))
        {
            budget.ReserveString(text.Length - fix.Length);
            text = text[fix.Length..];
        }
        if (right && text.EndsWith(fix, StringComparison.Ordinal))
        {
            budget.ReserveString(text.Length - fix.Length);
            text = text[..^fix.Length];
        }
        return ReferenceEquals(String(scalar), text) ? scalar : JsonValue.Create(text);
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

    internal JsonNode TrimSides(JsonNode? input, bool left, bool right)
    {
        if (input is not JsonValue scalar || !TryGetString(input, out string? text) || text is null)
            throw new JqException("trim input must be a string");
        budget.CheckCancellation();
        int start = 0;
        int end = text.Length;
        int scanned = 0;
        if (left)
        {
            while (start < end)
            {
                if ((scanned++ & 4095) == 0) budget.CheckCancellation();
                if (Rune.DecodeFromUtf16(text.AsSpan(start, end - start), out Rune rune, out int consumed)
                    != System.Buffers.OperationStatus.Done || !IsJqWhitespace(rune.Value)) break;
                start += consumed;
            }
        }
        if (right)
        {
            while (end > start)
            {
                if ((scanned++ & 4095) == 0) budget.CheckCancellation();
                if (Rune.DecodeLastFromUtf16(text.AsSpan(start, end - start), out Rune rune, out int consumed)
                    != System.Buffers.OperationStatus.Done || !IsJqWhitespace(rune.Value)) break;
                end -= consumed;
            }
        }
        budget.CheckCancellation();
        if (start == 0 && end == text.Length) return scalar;
        budget.ReserveString(end - start);
        return JsonValue.Create(text[start..end]);
    }

    internal JsonNode Explode(JsonNode? input)
    {
        if (!TryGetString(input, out string? text) || text is null)
            throw new JqException("explode input must be a string");
        var arr = new JsonArray();
        foreach (var rune in text.EnumerateRunes())
        {
            budget.CheckCancellation();
            budget.ReserveBytes(88);
            arr.Add(rune.Value);
        }
        return arr;
    }

    internal JsonNode Implode(JsonNode? input)
    {
        if (input is not JsonArray arr)
            throw new JqException("implode input must be an array");
        long length = 0;
        for (int index = 0; index < arr.Count; index++)
        {
            if ((index & 4095) == 0) budget.CheckCancellation();
            length += Scalar(arr[index]) > 0xFFFF ? 2 : 1;
        }
        budget.ReserveString(length);
        var sb = new StringBuilder((int)length);
        Span<char> encoded = stackalloc char[2];
        for (int index = 0; index < arr.Count; index++)
        {
            if ((index & 4095) == 0) budget.CheckCancellation();
            int count = new Rune(Scalar(arr[index])).EncodeToUtf16(encoded);
            sb.Append(encoded[..count]);
        }
        return JsonValue.Create(budget.Finish(sb));

        int Scalar(JsonNode? item)
        {
            long point;
            if (item is JsonValue small && small.TryGetValue<int>(out int direct))
                point = direct;
            else if (item is JsonValue number && number.TryGetValue<long>(out long whole))
                point = whole;
            else if (item is JsonValue real && real.TryGetValue<double>(out double value) && !double.IsNaN(value))
                point = (long)value;
            else
                throw new JqException($"{TypeName(item)} ({TruncatedDump(item)}) can't be imploded, unicode codepoint needs to be numeric");
            return point < 0 || point > 0x10FFFF || (point >= 0xD800 && point <= 0xDFFF) ? 0xFFFD : (int)point;
        }
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
                budget.CheckCancellation();
                budget.ReserveBytes(88);
                budget.ReserveString(rune.Utf16SequenceLength);
                arr.Add(rune.ToString());
            }
            return arr;
        }
        var start = 0;
        while (true)
        {
            var end = FindStringIndex(text, delimiter, start, false);
            var length = (end < 0 ? text.Length : end) - start;
            budget.CheckCancellation();
            budget.ReserveBytes(88);
            if (start != 0 || length != text.Length) budget.ReserveString(length);
            budget.CheckCancellation();
            arr.Add(text.Substring(start, length));
            if (end < 0) break;
            start = end + delimiter.Length;
        }
        return arr;
    }

    internal JsonNode Join(JsonNode? input, JsonNode? separator)
    {
        if (input is not (JsonArray or JsonObject))
            throw new JqRuntimeException("Cannot iterate over " + TypeName(input) + " (" + TruncatedDump(input) + ")");
        bool hasSepText = separator is null;
        string sepText = string.Empty;
        if (separator is not null && TryGetString(separator, out string? sepCandidate) && sepCandidate is not null)
        {
            hasSepText = true;
            sepText = sepCandidate;
        }
        var builder = new StringBuilder();
        bool first = true;
        foreach (JsonNode? item in IteratorFilter.ReadValues(input, false, budget))
        {
            if (!first)
            {
                if (!hasSepText)
                {
                    throw new JqRuntimeException("string (" + PreviewAccumulator() + ") and " + TypeName(separator) + " (" + TruncatedDump(separator) + ") cannot be added");
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
                throw new JqRuntimeException("string (" + PreviewAccumulator() + ") and " + TypeName(item) + " (" + TruncatedDump(item) + ") cannot be added");
            }
            budget.Append(builder, elemText);
            first = false;
        }
        return JsonValue.Create(budget.Finish(builder));

        string PreviewAccumulator()
        {
            // One extra unit lets the shared renderer apply its surrogate rule.
            int length = Math.Min(DiagnosticPreviewBytes + 1, builder.Length);
            budget.ReserveString(length);
            budget.CheckCancellation();
            return TruncatedDump(JsonValue.Create(builder.ToString(0, length)));
        }
    }

    // Numbers that are whole and fit in a long keep integral storage like
    // literals and inputs; anything else stays a double.
    internal static JsonNode CreateNumber(double value)
    {
        // Int64.MaxValue rounds up to 2^63 as a double; that bound is exclusive.
        // Negative zero needs double storage to retain its sign and path identity.
        if (double.IsFinite(value) && (value != 0 || !double.IsNegative(value))
            && value == Math.Truncate(value) && value >= long.MinValue && value < -(double)long.MinValue)
            return JsonValue.Create((long)value);
        return JsonValue.Create(value);
    }

    internal JsonNode FormatValue(string format, JsonNode? value) =>
        format == "text" && value is JsonValue scalar && TryGetString(scalar, out _)
            ? scalar : JsonValue.Create(Format(format, value));

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
            case "uri": return UriEncode();
            case "base64":
            {
                var bytes = JqBudget.BufferCapacity(Utf8Text.GetByteCount(text, budget.CancellationToken), 0);
                budget.ReserveString(4 * ((bytes + 2L) / 3));
                budget.ReserveBytes(bytes);
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            }
            case "base64d": return Base64Decode(text, value);
            default: throw new JqException($"unsupported format @{format}");
        }

        string UriEncode()
        {
            budget.CheckCancellation();
            long length = 0;
            int scanned = 0;
            foreach (Rune rune in text.EnumerateRunes())
            {
                if ((scanned++ & 4095) == 0) budget.CheckCancellation();
                length += rune.Value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or '~'
                    ? 1 : 3L * rune.Utf8SequenceLength;
            }
            budget.CheckCancellation();
            if (length == text.Length) return text;
            budget.ReserveString(length);
            budget.CheckCancellation();
            return string.Create((int)length, (Text: text, Budget: budget), static (destination, state) =>
            {
                int offset = 0;
                for (int start = 0; start < state.Text.Length;)
                {
                    state.Budget.CheckCancellation();
                    int count = Math.Min(1024, state.Text.Length - start);
                    if (start + count < state.Text.Length && char.IsHighSurrogate(state.Text[start + count - 1])) count--;
                    if (!Uri.TryEscapeDataString(state.Text.AsSpan(start, count), destination[offset..], out int written))
                        throw new InvalidOperationException("URI encoding exceeded its preflight length.");
                    offset += written;
                    start += count;
                }
                state.Budget.CheckCancellation();
            });
        }

        // Upstream escapes only ampersand, angle brackets, quote and apostrophe,
        // leaving UTF-8 bytes raw. Codes: 38 is &, 60 is <, 62 is >, 39 is apostrophe, 34 is quote.
        string Html(string source)
        {
            budget.CheckCancellation();
            long length = source.Length;
            for (int index = 0; index < source.Length; index++)
            {
                if ((index & 4095) == 0) budget.CheckCancellation();
                ReadOnlySpan<char> escaped = Escape(source[index]);
                if (!escaped.IsEmpty) length += escaped.Length - 1;
            }
            budget.CheckCancellation();
            if (length == source.Length) return source;
            budget.ReserveString(length);
            budget.CheckCancellation();
            return string.Create((int)length, (Text: source, Budget: budget), static (destination, state) =>
            {
                int offset = 0;
                for (int index = 0; index < state.Text.Length; index++)
                {
                    if ((index & 4095) == 0) state.Budget.CheckCancellation();
                    char ch = state.Text[index];
                    ReadOnlySpan<char> escaped = Escape(ch);
                    if (escaped.IsEmpty) destination[offset++] = ch;
                    else
                    {
                        escaped.CopyTo(destination[offset..]);
                        offset += escaped.Length;
                    }
                }
                state.Budget.CheckCancellation();
            });

            static ReadOnlySpan<char> Escape(char ch) => ch switch
            {
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '\'' => "&apos;", '"' => "&quot;", _ => ""
            };
        }

        // Manual decoder matching the reference: stops at the first padding mark,
        // rejects non-alphabet bytes, and reports a dangling single quantum.
        // Codes: 61 is =, 65 to 90 are A-Z, 97 to 122 are a-z, 48 to 57 are 0-9, 43 is +, 47 is /.
        string Base64Decode(string source, JsonNode? original)
        {
            budget.CheckCancellation();
            int length = 0;
            while (length < source.Length && source[length] != '=')
            {
                if ((length & 4095) == 0) budget.CheckCancellation();
                if (Base64Value(source[length]) < 0)
                    throw new JqException(TypeName(original) + " (" + TruncatedDump(original) + ") is not valid base64 data");
                length++;
            }
            if ((length & 3) == 1)
                throw new JqException(TypeName(original) + " (" + TruncatedDump(original) + ") trailing base64 byte found");
            int capacity = JqBudget.BufferCapacity(3L * length / 4, 0);
            using var memory = new JqMemoryReservation(budget.MemoryBudget);
            if (capacity != 0) memory.Resize(capacity);
            budget.CheckCancellation();
            byte[] decoded = capacity == 0 ? [] : new byte[capacity];
            int written = 0;
            uint code = 0;
            int pending = 0;
            for (int index = 0; index < length; index++)
            {
                if ((index & 4095) == 0) budget.CheckCancellation();
                int digit = Base64Value(source[index]);
                code = (code << 6) | (uint)digit;
                pending++;
                if (pending == 4)
                {
                    decoded[written++] = (byte)((code >> 16) & 0xFF);
                    decoded[written++] = (byte)((code >> 8) & 0xFF);
                    decoded[written++] = (byte)(code & 0xFF);
                    pending = 0;
                    code = 0;
                }
            }
            if (pending == 3)
            {
                decoded[written++] = (byte)((code >> 10) & 0xFF);
                decoded[written++] = (byte)((code >> 2) & 0xFF);
            }
            else if (pending == 2)
            {
                decoded[written++] = (byte)((code >> 4) & 0xFF);
            }
            budget.ReserveString(Encoding.UTF8.GetCharCount(decoded.AsSpan(0, written)));
            // Invalid UTF-8 becomes U+FFFD, matching the reference replacement.
            return Encoding.UTF8.GetString(decoded.AsSpan(0, written));
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
            budget.CheckCancellation();
            long decodedLength = 0;
            for (int index = 0, scanned = 0; index < source.Length; index++, scanned++)
            {
                if ((scanned & 4095) == 0) budget.CheckCancellation();
                char ch = source[index];
                if (ch == '%')
                {
                    if (index + 2 >= source.Length || HexValue(source[index + 1]) < 0 || HexValue(source[index + 2]) < 0)
                        throw new JqException(TypeName(original) + " (" + TruncatedDump(original) + ") is not a valid uri encoding");
                    decodedLength++;
                    index += 2;
                }
                else if (ch < 128) decodedLength++;
                else
                {
                    // Invalid host UTF-16 consumes one unit as U+FFFD, like the encoder.
                    Rune.DecodeFromUtf16(source.AsSpan(index), out Rune rune, out int consumed);
                    decodedLength += rune.Utf8SequenceLength;
                    index += consumed - 1;
                }
            }
            int capacity = JqBudget.BufferCapacity(decodedLength, 0);
            using var memory = new JqMemoryReservation(budget.MemoryBudget);
            if (capacity != 0) memory.Resize(capacity);
            budget.CheckCancellation();
            byte[] decoded = capacity == 0 ? [] : new byte[capacity];
            int written = 0;
            for (int index = 0, scanned = 0; index < source.Length; index++, scanned++)
            {
                if ((scanned & 4095) == 0) budget.CheckCancellation();
                char ch = source[index];
                if (ch == (char)37)
                {
                    int hi = HexValue(source[index + 1]);
                    int lo = HexValue(source[index + 2]);
                    decoded[written++] = (byte)((hi << 4) | lo);
                    index += 2;
                }
                else if (ch < (char)128)
                {
                    decoded[written++] = (byte)ch;
                }
                else
                {
                    // Non-ASCII scalars pass through as their UTF-8 bytes.
                    int length = char.IsHighSurrogate(ch) && index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]) ? 2 : 1;
                    written += Encoding.UTF8.GetBytes(source.AsSpan(index, length), decoded.AsSpan(written));
                    index += length - 1;
                }
            }
            budget.ReserveString(Encoding.UTF8.GetCharCount(decoded.AsSpan(0, written)));
            try
            {
                return Utf8Text.Decode(decoded.AsMemory(0, written));
            }
            catch (DecoderFallbackException)
            {
                throw new JqException(TypeName(original) + " (" + TruncatedDump(original) + ") is not a valid uri encoding");
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
            if (element is null || TypeName(element) == "boolean")
            {
                budget.Append(builder, ToJqString(element));
                return;
            }
            if (TypeName(element) == "number")
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
            throw new JqException(TypeName(element) + " (" + TruncatedDump(element) + ") can not be escaped for shell");
        }

        // String fields are always quoted with embedded quotes doubled.
        string Csv(JsonNode? rows)
        {
            if (rows is not JsonArray array)
                throw new JqException(TypeName(rows) + " (" + TruncatedDump(rows) + ") cannot be csv-formatted, only array");
            var builder = new StringBuilder();
            for (int index = 0; index < array.Count; index++)
            {
                if (index > 0) budget.Append(builder, ",");
                JsonNode? field = array[index];
                if (field is null || IsNaNNumber(field)) continue;
                if (TypeName(field) == "boolean")
                {
                    budget.Append(builder, ToJqString(field));
                }
                else if (TypeName(field) == "number")
                {
                    budget.Append(builder, Serialize(field, false, null, false));
                }
                else if (TryGetString(field, out string? cell) && cell is not null)
                {
                    long length = cell.Length + 2L;
                    for (int position = 0; position < cell.Length; position++)
                    {
                        if ((position & 4095) == 0) budget.CheckCancellation();
                        if (cell[position] == '"') length++;
                    }
                    if (length > int.MaxValue - builder.Length)
                        throw new JqQuotaException("string result cannot be represented");
                    budget.ReserveString(length);
                    builder.Append('"');
                    for (int position = 0; position < cell.Length; position++)
                    {
                        if ((position & 4095) == 0) budget.CheckCancellation();
                        if (cell[position] == '"') builder.Append('"');
                        builder.Append(cell[position]);
                    }
                    builder.Append('"');
                }
                else
                {
                    throw new JqException(TypeName(field) + " (" + TruncatedDump(field) + ") is not valid in a csv row");
                }
            }
            return budget.Finish(builder);
        }

        string Tsv(JsonNode? rows)
        {
            if (rows is not JsonArray array)
                throw new JqException(TypeName(rows) + " (" + TruncatedDump(rows) + ") cannot be tsv-formatted, only array");
            var builder = new StringBuilder();
            for (var i = 0; i < array.Count; i++)
            {
                if (i > 0) budget.Append(builder, "\t");
                var field = array[i];
                if (field is null || IsNaNNumber(field)) continue;
                if (field is JsonArray or JsonObject)
                    throw new JqException(TypeName(field) + " (" + TruncatedDump(field) + ") is not valid in a csv row");

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
