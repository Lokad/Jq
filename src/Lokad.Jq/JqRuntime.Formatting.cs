using System.Buffers.Text;
using System.Text;

namespace Lokad.Jq;

internal sealed partial class JqRuntime
{
    internal JqString FormatValue(JqString format, JqValue? value)
    {
        ReadOnlySpan<byte> name = format.Utf8.Span;
        if (name.SequenceEqual("text"u8)) return FormatValue("text", value);
        if (name.SequenceEqual("json"u8)) return FormatValue("json", value);
        if (name.SequenceEqual("csv"u8)) return FormatValue("csv", value);
        if (name.SequenceEqual("tsv"u8)) return FormatValue("tsv", value);
        if (name.SequenceEqual("sh"u8)) return FormatValue("sh", value);
        if (name.SequenceEqual("html"u8)) return FormatValue("html", value);
        if (name.SequenceEqual("uri"u8)) return FormatValue("uri", value);
        if (name.SequenceEqual("urid"u8)) return FormatValue("urid", value);
        if (name.SequenceEqual("base64"u8)) return FormatValue("base64", value);
        if (name.SequenceEqual("base64d"u8)) return FormatValue("base64d", value);
        JqString message = JqString.Create(JqBudget.BufferCapacity((long)format.ByteLength + " is not a valid format"u8.Length, 0),
            format, static (output, name) =>
            {
                name.Utf8.Span.CopyTo(output);
                " is not a valid format"u8.CopyTo(output[name.ByteLength..]);
            }, budget);
        throw new JqErrorException(message);
    }

    internal JqString FormatValue(string format, JqValue? value)
    {
        budget.CheckCancellation();
        switch (format)
        {
            case "text": return ToNativeString(value);
            case "json": return ToJsonString(value);
            case "csv": case "tsv": case "sh": return FormatRow(value, format);
            case "html": return ToNativeString(value).Html(budget);
            case "uri": return ToNativeString(value).UriEncode(budget);
            case "base64": return ToNativeString(value).EncodeBase64(budget);
            case "base64d":
                if (ToNativeString(value).TryDecodeBase64(budget, out JqString? decoded, out bool dangling)) return decoded;
                throw FormatError(value, dangling ? "trailing base64 byte found" : "is not valid base64 data");
            case "urid":
                if (ToNativeString(value).TryDecodeUri(budget, out JqString? uri)) return uri;
                throw FormatError(value, "is not a valid uri encoding");
            default:
                const string suffix = " is not a valid format";
                budget.ReserveString((long)format.Length + suffix.Length);
                budget.CheckCancellation();
                throw new JqException(format + suffix);
        }
    }

    private JqException FormatError(JqValue? value, string suffix)
    {
        string kind = TypeName(value), preview = TruncatedDump(value);
        budget.ReserveString((long)kind.Length + preview.Length + suffix.Length + 4);
        return new JqException(kind + " (" + preview + ") " + suffix);
    }

    private JqString FormatRow(JqValue? value, string format)
    {
        JqArray? array = value as JqArray;
        if (array is null && format != "sh")
            throw FormatError(value, "cannot be " + format + "-formatted, only array");
        int count = array?.Count ?? 1;
        long length = Math.Max(0, count - 1);
        Span<byte> scalar = stackalloc byte[128];
        for (int index = 0; index < count; index++)
        {
            budget.CheckCancellation();
            JqValue? field = array is null ? value : array[index];
            if (format != "sh" && (field is null || IsNaNNumber(field))) continue;
            if (field is JqString text)
            {
                length += text.ByteLength + (format == "tsv" ? 0 : 2);
                for (int position = 0; position < text.ByteLength; position++)
                {
                    if ((position & 4095) == 0) budget.CheckCancellation();
                    int escaped = Escape(text.Utf8.Span[position], format).Length;
                    if (escaped != 0) length += escaped - 1;
                }
            }
            else if (field is null or JqScalar) length += FormatScalarUtf8(field, scalar);
            else throw FormatError(field, format == "sh" ? "can not be escaped for shell" : "is not valid in a csv row");
        }
        return JqString.Create(JqBudget.BufferCapacity(length, 0),
            (Array: array, Single: value, Format: format, Count: count, Budget: budget), static (output, state) =>
        {
            int written = 0;
            for (int index = 0; index < state.Count; index++)
            {
                state.Budget.CheckCancellation();
                if (index != 0) output[written++] = state.Format == "csv" ? (byte)',' : state.Format == "tsv" ? (byte)'\t' : (byte)' ';
                JqValue? field = state.Array is null ? state.Single : state.Array[index];
                if (state.Format != "sh" && (field is null || IsNaNNumber(field))) continue;
                if (field is not JqString text) { written += FormatScalarUtf8(field, output[written..]); continue; }
                byte quote = state.Format == "csv" ? (byte)'"' : (byte)'\'';
                if (state.Format != "tsv") output[written++] = quote;
                for (int position = 0; position < text.ByteLength; position++)
                {
                    if ((position & 4095) == 0) state.Budget.CheckCancellation();
                    byte value = text.Utf8.Span[position];
                    ReadOnlySpan<byte> escaped = Escape(value, state.Format);
                    if (escaped.IsEmpty) output[written++] = value;
                    else { escaped.CopyTo(output[written..]); written += escaped.Length; }
                }
                if (state.Format != "tsv") output[written++] = quote;
            }
        }, budget);

        static ReadOnlySpan<byte> Escape(byte value, string format) => format switch
        {
            "csv" => value == (byte)'"' ? "\"\""u8 : [],
            "sh" => value == (byte)'\'' ? "'\\''"u8 : [],
            _ => value switch { (byte)'\t' => "\\t"u8, (byte)'\n' => "\\n"u8, (byte)'\r' => "\\r"u8, (byte)'\\' => "\\\\"u8, _ => [] }
        };
    }

    private static int FormatScalarUtf8(JqValue? value, Span<byte> output)
    {
        ReadOnlySpan<byte> literal = value is null ? "null"u8 : [];
        if (value is JqScalar scalar)
        {
            if (scalar.TryGetValue<bool>(out bool boolean)) literal = boolean ? "true"u8 : "false"u8;
            else
            {
                int written;
                bool success;
                if (scalar.TryGetValue<int>(out int small)) success = Utf8Formatter.TryFormat(small, output, out written);
                else if (scalar.TryGetValue<long>(out long whole)) success = Utf8Formatter.TryFormat(whole, output, out written);
                else if (scalar.TryGetValue<decimal>(out decimal dec)) success = Utf8Formatter.TryFormat(dec, output, out written);
                else if (scalar.TryGetValue<double>(out double number))
                {
                    if (double.IsNaN(number)) { "null"u8.CopyTo(output); return 4; }
                    if (double.IsInfinity(number)) number = number > 0 ? double.MaxValue : double.MinValue;
                    success = Utf8Formatter.TryFormat(number, output, out written);
                }
                else throw new InvalidOperationException("Expected an immediate scalar.");
                return success ? written : throw new InvalidOperationException("Scalar formatting exceeded its admitted output.");
            }
        }
        literal.CopyTo(output);
        return literal.Length;
    }

    internal JqValue Join(JqValue? input, JqValue? separator)
    {
        if (input is not (JqArray or JqObject))
        {
            string kind = TypeName(input), preview = TruncatedDump(input);
            budget.ReserveString((long)kind.Length + preview.Length + 23);
            throw new JqException("Cannot iterate over " + kind + " (" + preview + ")");
        }
        using var builder = budget.RentUtf8Builder();
        JqJsonBuffer output = builder.Value;
        bool first = true;
        Span<byte> scalar = stackalloc byte[128];
        foreach (JqValue? item in IteratorFilter.ReadValues(input, false, budget))
        {
            budget.CheckCancellation();
            if (!first)
            {
                if (separator is JqString delimiter) Append(delimiter.Utf8.Span);
                else if (separator is not null) throw JoinError(separator);
            }
            if (item is JqString text) Append(text.Utf8.Span);
            else if (item is JqScalar)
            {
                Append(scalar[..FormatScalarUtf8(item, scalar)]);
            }
            else if (item is not null) throw JoinError(item);
            first = false;
        }
        return JqString.FromUtf8(output.WrittenMemory.Span, budget);

        void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return;
            JqBudget.BufferCapacity((long)output.WrittenMemory.Length + bytes.Length, 0);
            bytes.CopyTo(output.GetSpan(bytes.Length));
            output.Advance(bytes.Length);
        }
        JqException JoinError(JqValue? bad)
        {
            ReadOnlySpan<byte> bytes = output.WrittenMemory.Span;
            int count = Math.Min(DiagnosticPreviewBytes + 1, bytes.Length);
            while (count < bytes.Length && count > 0 && (bytes[count] & 0xc0) == 0x80) count--;
            JqString prefix = JqString.FromUtf8(bytes[..count], budget);
            return new JqException(TypeError(prefix, bad, "cannot be added"));
        }
    }
}
