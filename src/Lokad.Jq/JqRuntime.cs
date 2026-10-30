using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal sealed partial class JqRuntime(JqBudget budget)
{
    private const int DiagnosticPreviewBytes = 30;

    internal JqMemoryValue<ReadOnlyMemory<byte>> SerializeUtf8(JqValue? node, bool ascii, int? indent, bool tabs) =>
        SerializeUtf8(node, ascii, indent, tabs, false);

    internal JqMemoryValue<ReadOnlyMemory<byte>> SerializeUtf8(JqValue? node, bool ascii, int? indent, bool tabs, bool sorted) =>
        SerializeUtf8(node, ascii, indent, tabs, sorted, false, false);

    internal JqMemoryValue<ReadOnlyMemory<byte>> SerializeDebug(JqValue? value, bool ascii, bool sorted) =>
        SerializeUtf8(value, ascii, null, false, sorted, false, true);

    internal JqMemoryValue<ReadOnlyMemory<byte>> SerializeLine(JqValue? value) =>
        SerializeUtf8(value, false, null, false, false, false, false, true);

    private JqMemoryValue<ReadOnlyMemory<byte>> SerializeUtf8(JqValue? node, bool ascii, int? indent, bool tabs, bool sorted, bool preview, bool debug) =>
        SerializeUtf8(node, ascii, indent, tabs, sorted, preview, debug, false);

    private JqMemoryValue<ReadOnlyMemory<byte>> SerializeUtf8(JqValue? node, bool ascii, int? indent, bool tabs, bool sorted, bool preview, bool debug, bool newline)
    {
        using var keyOrder = sorted ? new JqComparer(budget) : null;
        JqJsonBuffer buffer = budget.RentUtf8Buffer();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, CreateWriterOptions(ascii, indent, tabs)))
            {
                try { WriteJson(node, ascii, writer, keyOrder, preview, debug); }
                finally { writer.Reset(); }
            }
            buffer.LowercaseUnicodeEscapes();
            if (debug || newline)
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

    private void WriteJson(JqValue? node, bool ascii, Utf8JsonWriter writer, JqComparer? keyOrder, bool preview, bool debug)
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

        static bool WriteValue(JqValue? value, int depth, JqBudget budget, bool ascii,
            Utf8JsonWriter writer, JqComparer? keyOrder, bool preview)
        {
            budget.CheckCancellation();
            JqRecursion.CheckValueDepth(value, depth);
            if (value is JqArray array)
            {
                writer.WriteStartArray();
                if (Full()) return false;
                foreach (JqValue? child in array)
                    if (!WriteValue(child, depth + 1, budget, ascii, writer, keyOrder, preview)) return false;
                writer.WriteEndArray();
            }
            else if (value is JqObject obj)
            {
                writer.WriteStartObject();
                if (Full()) return false;
                if (keyOrder is not null && obj.Count > 1)
                {
                    foreach (JqString key in keyOrder.OrderKeys(obj, depth))
                        if (!WriteProperty(key, obj[key])) return false;
                }
                else
                    foreach (var property in obj)
                        if (!WriteProperty(property.Key, property.Value)) return false;
                writer.WriteEndObject();
            }
            else if (value is null)
            {
                writer.WriteNullValue();
            }
            else if (value is JqScalar scalar && scalar.TryGetValue<double>(out double number) && !double.IsFinite(number))
            {
                if (double.IsNaN(number)) writer.WriteNullValue();
                else writer.WriteNumberValue(number > 0 ? double.MaxValue : double.MinValue);
            }
            else if (preview && value is JqString text)
                writer.WriteStringValue(Prefix(text.Utf8.Span));
            else ((JqScalar)value).WriteTo(writer);
            return !Full();

            bool Full() => preview && writer.BytesCommitted + writer.BytesPending >= DiagnosticPreviewBytes;

            bool WriteProperty(JqString name, JqValue? child)
            {
                budget.CheckCancellation();
                ReadOnlySpan<byte> key = Prefix(name.Utf8.Span);
                writer.WritePropertyName(key);
                if (key.Length != name.ByteLength || Full()) return false;
                return WriteValue(child, depth + 1, budget, ascii, writer, keyOrder, preview);
            }

            ReadOnlySpan<byte> Prefix(ReadOnlySpan<byte> text)
            {
                int length = preview ? Math.Min(DiagnosticPreviewBytes, text.Length) : text.Length;
                while (length < text.Length && length > 0 && (text[length] & 0xc0) == 0x80) length--;
                return text[..length];
            }
        }
    }

    internal JqValue? Clone(JqValue? node)
    {
        return budget.CopyValue(node, 0);
    }

    internal JqValue? Clone(JqValue? node, int depth) => budget.CopyValue(node, depth);

    // Single-pass decode: builds values while charging, so duplicate object
    // keys resolve last-wins at first position like object assignment.
    internal JqValue? ReadJsonValue(ReadOnlySpan<byte> text, out int consumed) =>
        ReadJsonValue(text, 0, out consumed);

    internal JqArray ReadJsonArray(ReadOnlySpan<byte> bytes, int childDepth)
    {
        budget.ReserveBytes(64);
        var values = new JqArray();
        int offset = HasBomPrefix(bytes) ? 3 : 0;
        while (offset < bytes.Length)
        {
            budget.CheckCancellation();
            if (bytes[offset..].TrimStart(" \t\r\n"u8).IsEmpty) break;
            JqValue? value = ReadJsonValue(bytes[offset..], childDepth, out int consumed);
            offset += consumed;
            // Parsing already owns the node; only the containing slot is new.
            budget.ReserveBytes(24);
            values.Add(value);
        }
        return values;
    }

    internal JqValue? ReadJsonValue(ReadOnlySpan<byte> text, int depth, out int consumed)
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
                return JqScalar.Create(topValue);
            }
            if (TryInvalidNonFiniteLiteral(text, firstContent, default, out string invalidMessage))
                throw new JqException(invalidMessage);
            if (!reader.Read())
                throw new JqException("expected a JSON value");
            int consumedBase = 0;
            JqValue? value = ReadValue(ref reader, text, ref consumedBase, depth);
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
    private void ReadToken(ref Utf8JsonReader reader, ReadOnlySpan<byte> source, ref int consumedBase, InputPrefix prefix, JsonReaderState restoreState, bool consumeComma, out bool hasToken, out JqValue? nonFinite)
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
                    nonFinite = JqScalar.Create(number);
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

        JqValue? ReadValue(ref Utf8JsonReader reader, ReadOnlySpan<byte> source, ref int consumedBase, int depth)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    JqRecursion.CheckDepth(depth + 1, "value nesting limit exceeded");
                    budget.ReserveValueNode();
                    var obj = new JqObject(budget);
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
                        JqString name = JqString.FromJson(ref reader, budget);
                        budget.ReserveBytes(96);
                        ReadToken(ref reader, source, ref consumedBase, default, headState, expectsObjectValue, out bool hasValue, out JqValue? valueFallback);
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
                    var array = new JqArray();
                    bool expectsValue = true;
                    while (true)
                    {
                        JsonReaderState headState = reader.CurrentState;
                        ReadToken(ref reader, source, ref consumedBase, default, headState, expectsValue, out bool hasToken, out JqValue? nonFinite);
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

    private JqValue? ReadScalar(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number
            or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null))
            throw new JqException("expected a JSON value");
        if (reader.TokenType == JsonTokenType.Null) budget.CheckCancellation();
        else if (reader.TokenType != JsonTokenType.String) budget.ReserveValueNode();
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return JqString.FromJson(ref reader, budget);
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long whole))
                {
                    if (whole == 0 && reader.ValueSpan.Length > 0 && reader.ValueSpan[0] == (byte)'-')
                        return JqScalar.Create(reader.GetDouble());
                    return JqScalar.Create(whole);
                }
                return JqScalar.Create(reader.GetDouble());
            case JsonTokenType.True:
                return JqScalar.Create(true);
            case JsonTokenType.False:
                return JqScalar.Create(false);
            case JsonTokenType.Null:
                return null;
            default:
                throw new JqException("expected a JSON value");
        }
    }

    // Like the reference fromjson, only strings parse; other inputs fail
    // with the upstream diagnostic before any decoding.
    internal JqValue? ParseJson(JqValue? input)
    {
        if (input is not JqString text)
            throw new JqException($"{TypeName(input)} ({TruncatedDump(input)}) only strings can be parsed");
        ReadOnlySpan<byte> bytes = text.Utf8.Span;
        int prefix = HasBomPrefix(bytes) ? 3 : 0;
        JqValue? value;
        int consumed;
        try { value = ReadJsonValue(bytes[prefix..], out consumed); }
        catch (JqException exception) when (exception is not JqQuotaException
            && exception.Message.StartsWith("Invalid numeric literal at EOF", StringComparison.Ordinal))
        {
            string source = text.ToHost(budget);
            budget.ReserveString(source.Length + (long)exception.Message.Length + 20);
            throw new JqException(exception.Message + " (while parsing '" + source + "')");
        }
        if (!bytes[(prefix + consumed)..].Trim(" \t\r\n"u8).IsEmpty) throw new JqException("expected a single JSON value");
        return value;
    }

    internal JqValue? ParseJson(string text)
    {
        budget.CheckCancellation();
        int capacity = JqBudget.BufferCapacity(Utf8Text.GetByteCount(text, budget.CancellationToken), 0);
        using var memory = new JqMemoryReservation(budget.MemoryBudget);
        if (capacity != 0) memory.Resize(capacity);
        budget.CheckCancellation();
        var bytes = Encoding.UTF8.GetBytes(text);
        int prefix = HasBomPrefix(bytes) ? 3 : 0;
        int consumed;
        JqValue? node;
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
    internal string TypeError(JqValue? l, JqValue? r, string verb) =>
        $"{TypeName(l)} ({TruncatedDump(l)}) and {TypeName(r)} ({TruncatedDump(r)}) {verb}";

    // Reference-shaped operand dump for unary type errors (negation and its
    // parser/math aliases share the message).
    internal string UnaryTypeError(JqValue? value, string verb) =>
        $"{TypeName(value)} ({TruncatedDump(value)}) {verb}";

    // Mirrors jv_dump_string_trunc with a 30-byte buffer: dumps longer than
    // 29 bytes keep 25 bytes (26 without a closing delimiter for scalars
    // other than strings) backed over the UTF-8 boundary, then take "..."
    // with the closing delimiter. The shared writer stops after obtaining
    // that prefix, including when a string or property name is much longer.
    internal string TruncatedDump(JqValue? node)
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

    internal JqValue? Add(JqValue? l, JqValue? r)
    {
        budget.CheckCancellation();
        if (l == null) return r;
        if (r == null) return l;
        if (TypeName(l) == "number" && TypeName(r) == "number")
            return JqScalar.Create(Number(l) + Number(r));
        if (l is JqString ls && r is JqString rs) return ls.Concat(rs, budget);
        if (l is JqArray la && r is JqArray ra)
        {
            if (ra.Count == 0) return la;
            var arr = new JqArray();
            foreach (var value in la.Concat(ra))
            {
                budget.ReserveArrayItem(value);
                arr.Add(budget.CopyValue(value, 1));
            }
            return arr;
        }
        if (l is JqObject lo && r is JqObject ro)
            return MergeObjects(lo, ro, false, 0);
        throw new JqException(TypeError(l, r, "cannot be added"));
    }

    internal JqValue? Subtract(JqValue? l, JqValue? r)
    {
        if (TypeName(l) == "number" && TypeName(r) == "number")
            return JqScalar.Create(Number(l) - Number(r));
        if (l is JqArray la && r is JqArray ra)
        {
            var result = new JqArray();
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
        throw new JqException(TypeError(l, r, "cannot be subtracted"));
    }

    internal JqValue? Multiply(JqValue? l, JqValue? r)
    {
        if (TypeName(l) == "number" && TypeName(r) == "number")
            return JqScalar.Create(Number(l) * Number(r));
        if (l is JqString leftText && TypeName(r) == "number")
            return Repeat(leftText, Number(r));
        if (r is JqString rightText && TypeName(l) == "number")
            return Repeat(rightText, Number(l));
        if (l is JqObject lo && r is JqObject ro)
            return MergeObjects(lo, ro, true, 0);
        throw new JqException(TypeError(l, r, "cannot be multiplied"));
    }

    private JqValue? Repeat(JqString value, double count)
    {
        int times = double.IsNaN(count) || count < 0 ? -1 : count > int.MaxValue ? int.MaxValue : (int)count;
        return times < 0 ? null : value.Repeat(times, budget);
    }

    private JqObject MergeObjects(JqObject left, JqObject right, bool recursive, int depth)
    {
        budget.CheckCancellation();
        // Nested results need private nodes for their new parent; the root can borrow.
        if (depth == 0 && right.Count == 0) return left;
        JqRecursion.CheckValueDepth(left, depth);
        JqRecursion.CheckValueDepth(right, depth);
        budget.ReserveBytes(64 + 96L * left.Count);
        var merged = new JqObject(budget);
        foreach (var property in left)
        {
            budget.CheckCancellation();
            bool replaced = right.TryGetPropertyValue(property.Key, out JqValue? incoming);
            if (recursive && property.Value is JqObject existingObject && incoming is JqObject incomingObject)
                merged.Add(property.Key, MergeObjects(existingObject, incomingObject, true, depth + 1));
            else
            {
                JqValue? selected = replaced ? incoming : property.Value;
                if (selected is JqScalar) budget.ReserveBytes(64);
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
    internal static double Number(JqValue? node)
    {
        if (node is JqScalar v && v.TryGetValue<double>(out var d)) return d;
        if (node is JqScalar i && i.TryGetValue<int>(out var intValue)) return intValue;
        if (node is JqScalar l && l.TryGetValue<long>(out var longValue)) return longValue;
        if (node is JqScalar m && m.TryGetValue<decimal>(out var decimalValue)) return (double)decimalValue;
        throw new JqException($"expected number, got {TypeName(node)}");
    }

    internal JqValue ToJsonNumber(JqValue? node)
    {
        budget.CheckCancellation();
        if (node is JqScalar numberValue && TypeName(numberValue) == "number")
            return numberValue;
        if (node is JqString text)
        {
            if (TryParseNumberText(text.Utf8.Span, out JqValue? number) && number is not null)
                return number;
            if (TryParseNonFiniteNumber(text.Utf8.Span, out JqValue? nonFinite) && nonFinite is not null)
                return nonFinite;
        }
        throw new JqException($"{TypeName(node)} ({TruncatedDump(node)}) cannot be parsed as a number");
    }

    // Non-finite spellings mirror the reference strtod fallback (builtin.c
    // f_tonumber through jvp_strtod INFNAN_CHECK): an optional sign with a
    // case-insensitive nan, inf, or infinity and no surrounding whitespace.
    // Hex floats and NaN payloads stay rejected.
    private static bool TryParseNonFiniteNumber(ReadOnlySpan<byte> text, out JqValue? number)
    {
        number = null;
        ReadOnlySpan<byte> rest = text;
        bool negative = false;
        if (rest.Length > 0 && (rest[0] == (byte)'+' || rest[0] == (byte)'-'))
        {
            negative = rest[0] == (byte)'-';
            rest = rest.Slice(1);
        }
        if (Ascii.EqualsIgnoreCase(rest, "nan"u8))
        {
            number = JqScalar.Create(double.NaN);
            return true;
        }
        if (Ascii.EqualsIgnoreCase(rest, "inf"u8) || Ascii.EqualsIgnoreCase(rest, "infinity"u8))
        {
            number = JqScalar.Create(negative ? double.NegativeInfinity : double.PositiveInfinity);
            return true;
        }
        return false;
    }

    // jq numeric text permits a leading plus, omitted whole digits and leading
    // zeros, but no surrounding whitespace. Nonzero integers keep exact storage.
    private bool TryParseNumberText(ReadOnlySpan<byte> text, out JqValue? number)
    {
        number = null;
        int position = 0;
        if (position < text.Length && (text[position] == (byte)'+' || text[position] == (byte)'-'))
            position++;
        int whole = position;
        ScanDigits(text, ref position, budget);
        bool hasWhole = position > whole;
        bool hasFraction = false;
        if (position < text.Length && text[position] == (byte)'.')
        {
            position++;
            int fraction = position;
            ScanDigits(text, ref position, budget);
            hasFraction = position > fraction;
            if (!hasFraction)
                return false;
        }
        if (!hasWhole && !hasFraction)
            return false;
        bool hasExponent = position < text.Length && (text[position] == (byte)'e' || text[position] == (byte)'E');
        if (hasExponent)
        {
            position++;
            if (position < text.Length && (text[position] == (byte)'+' || text[position] == (byte)'-'))
                position++;
            int exponent = position;
            ScanDigits(text, ref position, budget);
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
                number = wholeValue == 0 && text[0] == (byte)'-' ? JqScalar.Create(-0.0) : JqScalar.Create(wholeValue);
                return true;
            }
        }
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
        budget.CheckCancellation();
        if (parsed)
        {
            number = JqScalar.Create(value);
            return true;
        }
        return false;

        static void ScanDigits(ReadOnlySpan<byte> text, ref int position, JqBudget budget)
        {
            while (position < text.Length && text[position] is >= (byte)'0' and <= (byte)'9')
            {
                if ((position & 4095) == 0) budget.CheckCancellation();
                position++;
            }
        }
    }

    internal bool ToJsonBoolean(JqValue? node)
    {
        budget.CheckCancellation();
        if (node is JqScalar boolean && boolean.TryGetValue<bool>(out bool value))
            return value;
        if (node is JqString text)
        {
            if (text.Utf8.Span.SequenceEqual("true"u8))
                return true;
            if (text.Utf8.Span.SequenceEqual("false"u8))
                return false;
        }
        throw new JqException($"{TypeName(node)} ({TruncatedDump(node)}) cannot be parsed as a boolean");
    }

    internal static long Utf8ByteLength(JqValue? input, JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Budget.CheckCancellation();
        if (input is JqString text)
            return text.ByteLength;
        throw new JqException($"{TypeName(input)} ({context.Runtime.TruncatedDump(input)}) only strings have UTF-8 byte length");
    }


    internal JqString ToNativeString(JqValue? value) => value is JqString text ? text : ToJsonString(value);

    internal JqString ToJsonString(JqValue? value)
    {
        using var serialized = SerializeUtf8(value, false, null, false);
        return JqString.FromUtf8(serialized.Value.Span, budget);
    }

    internal static bool TryGetArrayIndex(JqValue? node, out long index)
    {
        index = 0;
        if (node is JqScalar small && small.TryGetValue<int>(out int direct))
        {
            index = direct;
            return true;
        }
        if (node is JqScalar whole && whole.TryGetValue<long>(out long directLong))
        {
            index = directLong;
            return true;
        }
        if (node is JqScalar real && real.TryGetValue<double>(out double value) && !double.IsNaN(value))
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

    internal static bool Truthy(JqValue? node)
    {
        if (node == null) return false;
        if (node is JqScalar v && v.TryGetValue<bool>(out var b)) return b;
        return true;
    }

    internal bool JsonEquals(JqValue? l, JqValue? r)
    {
        budget.CheckCancellation();
        return EqualsValue(l, r, 0);
    }

    // Mirrors the reference value equality: kind-sensitive, objects
    // order-insensitive, arrays ordered, numbers by double value with
    // Scalar NaN is unequal to itself; shared containers compare by identity.
    // Depth-bounded to protect recursive comparison: values deeper than the shared guard
    // fail cleanly instead of consuming CLR frames.
    private bool EqualsValue(JqValue? left, JqValue? right, int depth)
    {
        budget.CheckCancellation();
        if (left is JqArray or JqObject && ReferenceEquals(left, right)) return true;
        JqRecursion.CheckValueDepth(left, depth);
        JqRecursion.CheckValueDepth(right, depth);
        if (left is null || right is null)
            return left is null && right is null;
        if (left is JqArray leftArray && right is JqArray rightArray)
        {
            if (leftArray.Count != rightArray.Count)
                return false;
            for (var index = 0; index < leftArray.Count; index++)
                if (!EqualsValue(leftArray[index], rightArray[index], depth + 1))
                    return false;
            return true;
        }
        if (left is JqObject leftObject && right is JqObject rightObject)
        {
            if (leftObject.Count != rightObject.Count)
                return false;
            foreach (var property in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(property.Key, out JqValue? other))
                    return false;
                if (!EqualsValue(property.Value, other, depth + 1))
                    return false;
            }
            return true;
        }
        if (left is JqString leftText && right is JqString rightText)
            return leftText.ContentEquals(rightText, budget.CancellationToken);
        if (left is JqScalar leftScalar && right is JqScalar rightScalar
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
    internal int Compare(JqValue? left, JqValue? right) => JqComparer.CompareValues(left, right, budget);

    private static bool IsNaNNumber(JqValue? node) =>
        node?.GetValueKind() == JsonValueKind.Number && double.IsNaN(Number(node));

    internal JqValue? Length(JqValue? node)
    {
        budget.CheckCancellation();
        return node switch
        {
            null => JqScalar.Create(0),
            JqArray arr => JqScalar.Create(arr.Count),
            JqObject obj => JqScalar.Create(obj.Count),
            JqString text => JqScalar.Create(text.ScalarLength),
            // Like the reference number branch, numbers have absolute length.
            JqScalar v when TypeName(v) == "number" => JqScalar.Create(Math.Abs(Number(v))),
            _ => throw new JqException($"{TypeName(node)} ({TruncatedDump(node)}) has no length"),
        };
    }

    internal JqString SliceString(JqString text, double? start, double? end)
    {
        JqPaths.ResolveSlice(text.ScalarLength, start, end, out int from, out int to);
        return from == 0 && to == text.ScalarLength ? text.CloneHeader(budget) : text.Slice(from, to - from, budget);
    }

    internal static string TypeName(JqValue? node) => (node?.GetValueKind() ?? JsonValueKind.Null) switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.String => "string",
        _ => "number"
    };

    internal JqValue Keys(JqValue? input, bool sorted)
    {
        if (input is JqObject obj)
        {
            budget.ReserveBytes(64 + 88L * obj.Count);
            var keys = new JqArray();
            if (sorted)
            {
                using var comparer = new JqComparer(budget);
                foreach (JqString key in comparer.OrderKeys(obj, 0))
                {
                    budget.CheckCancellation();
                    keys.Add(JqScalar.Create(key));
                }
            }
            else
                foreach (var property in obj)
                {
                    budget.CheckCancellation();
                    keys.Add(JqScalar.Create(property.Key));
                }
            return keys;
        }
        if (input is JqArray arr)
        {
            budget.ReserveBytes(88L * arr.Count);
            var keys = new JqArray();
            for (int index = 0; index < arr.Count; index++)
            {
                budget.CheckCancellation();
                keys.Add(JqScalar.Create(index));
            }
            return keys;
        }
        throw new JqException($"{TypeName(input)} ({TruncatedDump(input)}) has no keys");
    }

    internal bool Has(JqValue? input, JqValue? key)
    {
        if (input is null)
            return false;
        if (input is JqObject obj && key is JqString name)
            return obj.ContainsKey(name);
        if (input is JqArray arr && JqPaths.TryGetIndex(key, out long index, out bool isNaN))
        {
            if (isNaN)
                return false;
            long clamped = index < int.MinValue ? int.MinValue : index > int.MaxValue ? int.MaxValue : index;
            return clamped >= 0 && clamped < arr.Count;
        }
        throw new JqException($"Cannot check whether {TypeName(input)} has a {TypeName(key)} key");
    }

    internal JqArray ToEntries(JqValue? input)
    {
        var entries = new JqArray();
        if (input is JqObject obj)
        {
            foreach (var property in obj)
            {
                budget.CheckCancellation();
                budget.ReserveBytes(248);
                budget.ReserveObjectItem(property.Value);
                var entry = new JqObject(budget);
                entry["key"] = JqScalar.Create(property.Key);
                entry["value"] = budget.CopyValue(property.Value, 2);
                entries.Add(entry);
            }
            return entries;
        }
        if (input is JqArray arr)
        {
            for (int index = 0; index < arr.Count; index++)
            {
                budget.CheckCancellation();
                budget.ReserveBytes(248);
                budget.ReserveObjectItem(arr[index]);
                var entry = new JqObject(budget);
                entry["key"] = JqScalar.Create(index);
                entry["value"] = budget.CopyValue(arr[index], 2);
                entries.Add(entry);
            }
            return entries;
        }
        throw new JqException($"{TypeName(input)} ({TruncatedDump(input)}) has no keys");
    }

    internal JqValue FromEntries(JqValue? input) =>
        FromEntryValues(IteratorFilter.ReadValues(input, false, budget));

    internal JqObject FromEntryValues(IEnumerable<JqValue?> entries)
    {
        var result = new JqObject(budget);
        foreach (JqValue? element in entries)
        {
            budget.CheckCancellation();
            JqValue? key = JqPathReads.ReadField(element, "key", this, budget);
            if (!Truthy(key)) key = JqPathReads.ReadField(element, "Key", this, budget);
            if (!Truthy(key)) key = JqPathReads.ReadField(element, "name", this, budget);
            if (!Truthy(key)) key = JqPathReads.ReadField(element, "Name", this, budget);
            if (key is not JqString keyName)
                throw new JqException($"Cannot use {TypeName(key)} ({TruncatedDump(key)}) as object key");
            budget.CheckCancellation();
            JqValue? value = element is JqObject entry && entry.TryGetPropertyValue("value", out JqValue? direct)
                ? direct : JqPathReads.ReadField(element, "Value", this, budget);
            budget.ReserveObjectItem(value);
            result[keyName] = budget.CopyValue(value, 1);
        }
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<JqValue?> AddValues(IAsyncEnumerable<JqValue?> values)
    {
        JqValue? result = null;
        bool ownsContainer = false;
        JqJsonBuffer? text = null;
        try
        {
            await foreach (JqValue? value in values.ConfigureAwait(false))
            {
                budget.CheckCancellation();
                if (text is not null)
                {
                    if (value is null) continue;
                    if (value is JqString piece)
                    {
                        Append(text, piece);
                        continue;
                    }
                    result = JqString.FromUtf8(text.WrittenMemory.Span, budget);
                    text.Dispose();
                    text = null;
                }
                if (ownsContainer && result is JqArray accumulated && value is JqArray incoming)
                {
                    // This accumulator was constructed here and has never escaped.
                    foreach (JqValue? item in incoming)
                    {
                        budget.ReserveArrayItem(item);
                        accumulated.Add(budget.CopyValue(item, 1));
                    }
                }
                else if (ownsContainer && result is JqObject merged && value is JqObject properties)
                {
                    foreach (var property in properties)
                    {
                        budget.ReserveObjectItem(property.Value);
                        merged[property.Key] = budget.CopyValue(property.Value, 1);
                    }
                }
                else if (result is JqString left && value is JqString right)
                {
                    text = new JqJsonBuffer(budget);
                    Append(text, left);
                    Append(text, right);
                    result = null;
                }
                else
                {
                    bool retainsOwned = ownsContainer && value is null;
                    bool combinesContainers = result is JqArray && value is JqArray
                        || result is JqObject && value is JqObject;
                    result = Add(result, value);
                    ownsContainer = retainsOwned || combinesContainers;
                }
            }
            return text is null ? result : JqString.FromUtf8(text.WrittenMemory.Span, budget);
        }
        finally { text?.Dispose(); }

        void Append(JqJsonBuffer destination, JqString piece)
        {
            budget.CheckCancellation();
            JqBudget.BufferCapacity((long)destination.WrittenMemory.Length + piece.ByteLength, 0);
            if (piece.ByteLength == 0) return;
            piece.Utf8.Span.CopyTo(destination.GetSpan(piece.ByteLength));
            destination.Advance(piece.ByteLength);
        }
    }

    internal JqValue Flatten(JqValue? input, JqValue? depth)
    {
        budget.CheckCancellation();
        if (input is not JqArray && input is not JqObject)
            throw new JqException($"cannot iterate over {TypeName(input)}");
        budget.ReserveBytes(64);
        var result = new JqArray();
        if (input is JqArray { Count: 0 } or JqObject { Count: 0 }) return result;
        bool numeric = FlattenFilter.TryDepthLevel(depth, out double level);
        using var cursor = new JqTreeCursor(budget);
        cursor.Push(input);
        while (cursor.Count != 0)
        {
            if (!cursor.TryNext(out JqValue? child))
            {
                cursor.Pop();
                continue;
            }
            if (child is JqArray nested)
            {
                // Nonnumeric depths fail only when a nested array needs depth - 1.
                if (!numeric) _ = Subtract(depth, JqScalar.Create(1));
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

    internal JqArray SortArray(JqValue? input) => OrderedArray(input, false);

    internal JqValue UniqueArray(JqValue? input) => OrderedArray(input, true);

    private JqArray OrderedArray(JqValue? input, bool unique)
    {
        if (input is not JqArray arr)
            throw new JqException("cannot be sorted, as it is not an array");
        budget.ReserveBytes(32L * arr.Count);
        var result = new JqArray();
        budget.CheckCancellation();
        using var comparer = new JqComparer(budget);
        JqValue? previous = null;
        bool first = true;
        foreach (JqValue? element in JqComparer.OrderBy(arr, static element => element, (IComparer<JqValue?>)comparer))
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

    internal JqValue? MinMax(JqValue? input, bool max)
    {
        budget.CheckCancellation();
        if (input is not JqArray arr)
            throw new JqException("cannot be iterated over");
        if (arr.Count == 0) return null;
        var best = arr[0];
        using var comparer = new JqComparer(budget);
        for (int index = 1; index < arr.Count; index++)
        {
            JqValue? item = arr[index];
            int order = comparer.Compare(item, best);
            if (max ? order >= 0 : order < 0)
                best = item;
        }
        return best;
    }

    internal JqValue Reverse(JqValue? input)
    {
        // Mirror the reference definition ([.[length-1-range(0;length)]]):
        // length-zero inputs yield [], arrays reverse natively, and anything
        // else fails staged (the reference raises index errors there).
        if (input is JqArray arr)
        {
            var rev = new JqArray();
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
            return new JqArray();
        }
        throw new JqException($"cannot reverse {TypeName(input)}");
    }

    internal bool Contains(JqValue? container, JqValue? contained)
    {
        if (TypeName(container) != TypeName(contained))
            throw new JqException($"{TypeName(container)} ({TruncatedDump(container)}) and {TypeName(contained)} ({TruncatedDump(contained)}) cannot have their containment checked");
        return ContainsSameKind(container, contained);
    }

    private bool ContainsSameKind(JqValue? container, JqValue? contained) => ContainsSameKind(container, contained, 0);

    // Depth-bounded to protect recursive comparison: values deeper than the shared guard
    // fail cleanly instead of consuming CLR frames.
    private bool ContainsSameKind(JqValue? container, JqValue? contained, int depth)
    {
        budget.CheckCancellation();
        JqRecursion.CheckValueDepth(container, depth);
        JqRecursion.CheckValueDepth(contained, depth);
        if (container is JqObject obj && contained is JqObject needle)
        {
            for (int index = 0; index < needle.Count; index++)
            {
                budget.CheckCancellation();
                var property = needle.GetAt(index);
                if (!obj.TryGetPropertyValue(property.Key, out JqValue? value)
                    || !ContainsSameKind(value, property.Value, depth + 1)) return false;
            }
            return true;
        }
        if (container is JqArray haystack && contained is JqArray needles)
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
        if (container is JqString text && contained is JqString fragment)
            return text.IndexOf(fragment, 0, budget.CancellationToken) >= 0;
        return JsonEquals(container, contained);
    }

    internal JqValue Indices(JqValue? input, JqValue? needle)
    {
        var arr = new JqArray();
        foreach (var index in MatchingIndices(input, needle))
        {
            budget.ReserveBytes(88);
            arr.Add(JqScalar.Create(index));
        }
        return arr;
    }

    internal JqValue? Index(JqValue? input, JqValue? needle, bool last)
    {
        budget.CheckCancellation();
        if (input is JqString text && needle is JqString fragment)
        {
            int found = fragment.ByteLength == 0 ? -1 : last ? text.LastIndexOf(fragment, budget.CancellationToken)
                : text.IndexOf(fragment, 0, budget.CancellationToken);
            return found < 0 ? null : JqScalar.Create(found);
        }
        int? position = null;
        foreach (int index in MatchingIndices(input, needle))
        {
            if (!last) return JqScalar.Create(index);
            position = index;
        }
        return position is { } result ? JqScalar.Create(result) : null;
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

    private IEnumerable<int> MatchingIndices(JqValue? input, JqValue? needle)
    {
        budget.CheckCancellation();
        if (input is JqString text && needle is JqString fragment)
        {
            foreach (int index in text.MatchingIndices(fragment, budget.CancellationToken)) yield return index;
            yield break;
        }
        if (input is JqArray values)
        {
            if (needle is JqArray pattern)
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
        throw new JqException($"cannot search {TypeName(input)}");
    }

    internal JqString AsciiCase(JqValue? input, bool lower)
    {
        if (input is not JqString text) throw new JqException("explode input must be a string");
        return text.AsciiCase(lower, budget);
    }

    internal bool StartsEndsWith(JqValue? input, JqValue? affix, bool fromStart)
    {
        budget.CheckCancellation();
        if (input is not JqString text || affix is not JqString fix)
            throw new JqException((fromStart ? "startswith" : "endswith") + "() requires string inputs");
        return fromStart
            ? text.Utf8.Span.StartsWith(fix.Utf8.Span)
            : text.Utf8.Span.EndsWith(fix.Utf8.Span);
    }

    internal JqValue TrimAffix(JqValue? input, JqValue? affix, bool left, bool right)
    {
        budget.CheckCancellation();
        if (input is not JqString text || affix is not JqString fix)
            throw new JqException((left ? "startswith" : "endswith") + "() requires string inputs");
        if (fix.ByteLength == 0) return text;
        int from = 0, to = text.ByteLength;
        if (left && text.Utf8.Span.StartsWith(fix.Utf8.Span)) from += fix.ByteLength;
        if (right && text.Utf8.Span[from..to].EndsWith(fix.Utf8.Span)) to -= fix.ByteLength;
        return from == 0 && to == text.ByteLength ? text : text.SliceBytes(from, to, budget);
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

    internal JqValue TrimSides(JqValue? input, bool left, bool right)
    {
        if (input is not JqString text) throw new JqException("trim input must be a string");
        int from = 0, to = text.ByteLength;
        if (left)
            while (from < to)
            {
                budget.CheckCancellation();
                Rune.DecodeFromUtf8(text.Utf8.Span[from..to], out Rune rune, out int width);
                if (!IsJqWhitespace(rune.Value)) break;
                from += width;
            }
        if (right)
            while (from < to)
            {
                budget.CheckCancellation();
                Rune.DecodeLastFromUtf8(text.Utf8.Span[from..to], out Rune rune, out int width);
                if (!IsJqWhitespace(rune.Value)) break;
                to -= width;
            }
        budget.CheckCancellation();
        return from == 0 && to == text.ByteLength ? text : text.SliceBytes(from, to, budget);
    }

    internal JqValue Explode(JqValue? input)
    {
        if (input is not JqString text) throw new JqException("explode input must be a string");
        budget.ReserveBytes(64);
        var result = new JqArray();
        foreach (int scalar in text.Scalars(budget.CancellationToken))
        {
            budget.ReserveBytes(88);
            result.Add(scalar);
        }
        return result;
    }

    internal JqValue Implode(JqValue? input)
    {
        if (input is not JqArray arr)
            throw new JqException("implode input must be an array");
        return JqString.FromScalars(arr, Scalar, budget);

        int Scalar(JqValue? item)
        {
            long point;
            if (item is JqScalar small && small.TryGetValue<int>(out int direct))
                point = direct;
            else if (item is JqScalar number && number.TryGetValue<long>(out long whole))
                point = whole;
            else if (item is JqScalar real && real.TryGetValue<double>(out double value) && !double.IsNaN(value))
                point = (long)value;
            else
                throw new JqException($"{TypeName(item)} ({TruncatedDump(item)}) can't be imploded, unicode codepoint needs to be numeric");
            return point < 0 || point > 0x10FFFF || (point >= 0xD800 && point <= 0xDFFF) ? 0xFFFD : (int)point;
        }
    }

    internal JqValue Split(JqValue? input, JqValue? separator)
    {
        if (input is not JqString text || separator is not JqString delimiter)
            throw new JqException("split input and separator must be strings");
        budget.ReserveBytes(64);
        var result = new JqArray();
        foreach (JqString piece in text.Split(delimiter, budget))
        {
            budget.ReserveBytes(24);
            result.Add(piece);
        }
        return result;
    }

    // Numbers that are whole and fit in a long keep integral storage like
    // literals and inputs; anything else stays a double.
    internal static JqValue CreateNumber(double value)
    {
        // Int64.MaxValue rounds up to 2^63 as a double; that bound is exclusive.
        // Negative zero needs double storage to retain its sign and path identity.
        if (double.IsFinite(value) && (value != 0 || !double.IsNegative(value))
            && value == Math.Truncate(value) && value >= long.MinValue && value < -(double)long.MinValue)
            return JqScalar.Create((long)value);
        return JqScalar.Create(value);
    }

}
