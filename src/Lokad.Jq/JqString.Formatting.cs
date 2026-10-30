using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Lokad.Jq;

internal sealed partial class JqString
{
    internal JqString Html(JqBudget budget)
    {
        long length = ByteLength;
        for (int index = 0; index < ByteLength; index++)
        {
            if ((index & 4095) == 0) budget.CheckCancellation();
            int escaped = Escape(_utf8.Span[index]).Length;
            if (escaped != 0) length += escaped - 1;
        }
        if (length == ByteLength) return CloneHeader(budget);
        return Create(JqBudget.BufferCapacity(length, 0), (Text: this, Budget: budget), static (output, state) =>
        {
            int written = 0;
            for (int index = 0; index < state.Text.ByteLength; index++)
            {
                if ((index & 4095) == 0) state.Budget.CheckCancellation();
                byte value = state.Text._utf8.Span[index];
                ReadOnlySpan<byte> escaped = Escape(value);
                if (escaped.IsEmpty) output[written++] = value;
                else { escaped.CopyTo(output[written..]); written += escaped.Length; }
            }
        }, budget);

        static ReadOnlySpan<byte> Escape(byte value) => value switch
        {
            (byte)'&' => "&amp;"u8, (byte)'<' => "&lt;"u8, (byte)'>' => "&gt;"u8,
            (byte)'\'' => "&apos;"u8, (byte)'"' => "&quot;"u8, _ => []
        };
    }

    internal JqString UriEncode(JqBudget budget)
    {
        long length = ByteLength;
        for (int index = 0; index < ByteLength; index++)
        {
            if ((index & 4095) == 0) budget.CheckCancellation();
            if (!Safe(_utf8.Span[index])) length += 2;
        }
        if (length == ByteLength) return CloneHeader(budget);
        return Create(JqBudget.BufferCapacity(length, 0), (Text: this, Budget: budget), static (output, state) =>
        {
            ReadOnlySpan<byte> hex = "0123456789ABCDEF"u8;
            int written = 0;
            for (int index = 0; index < state.Text.ByteLength; index++)
            {
                if ((index & 4095) == 0) state.Budget.CheckCancellation();
                byte value = state.Text._utf8.Span[index];
                if (Safe(value)) output[written++] = value;
                else
                {
                    output[written++] = (byte)'%';
                    output[written++] = hex[value >> 4];
                    output[written++] = hex[value & 15];
                }
            }
        }, budget);

        static bool Safe(byte value) => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';
    }

    internal JqString EncodeBase64(JqBudget budget)
    {
        int length = JqBudget.BufferCapacity(4L * ((ByteLength + 2L) / 3), 0);
        return Create(length, (Text: this, Budget: budget), static (output, state) =>
        {
            int source = 0, target = 0;
            while (source < state.Text.ByteLength)
            {
                state.Budget.CheckCancellation();
                int count = Math.Min(48 * 1024, state.Text.ByteLength - source);
                var status = Base64.EncodeToUtf8(state.Text._utf8.Span.Slice(source, count), output[target..],
                    out int consumed, out int written, source + count == state.Text.ByteLength);
                if (status != OperationStatus.Done || consumed != count)
                    throw new InvalidOperationException("Base64 encoding exceeded its admitted output.");
                source += consumed;
                target += written;
            }
        }, budget);
    }

    internal bool TryDecodeBase64(JqBudget budget, [NotNullWhen(true)] out JqString? result, out bool dangling)
    {
        result = null;
        dangling = false;
        int length = 0;
        while (length < ByteLength && _utf8.Span[length] != (byte)'=')
        {
            if ((length & 4095) == 0) budget.CheckCancellation();
            if (Digit(_utf8.Span[length]) < 0) return false;
            length++;
        }
        if ((length & 3) == 1) { dangling = true; return false; }
        int capacity = JqBudget.BufferCapacity(3L * length / 4, 0);
        using var memory = new JqMemoryReservation(budget.MemoryBudget);
        if (capacity != 0) memory.Resize(capacity);
        budget.CheckCancellation();
        byte[] decoded = new byte[capacity];
        int written = 0, pending = 0;
        uint code = 0;
        for (int index = 0; index < length; index++)
        {
            if ((index & 4095) == 0) budget.CheckCancellation();
            code = (code << 6) | (uint)Digit(_utf8.Span[index]);
            if (++pending != 4) continue;
            decoded[written++] = (byte)(code >> 16);
            decoded[written++] = (byte)(code >> 8);
            decoded[written++] = (byte)code;
            code = 0;
            pending = 0;
        }
        if (pending == 3)
        {
            decoded[written++] = (byte)(code >> 10);
            decoded[written++] = (byte)(code >> 2);
        }
        else if (pending == 2) decoded[written++] = (byte)(code >> 4);
        result = FromDecodedUtf8(decoded.AsMemory(0, written), budget);
        return true;

        static int Digit(byte value) => value switch
        {
            >= (byte)'A' and <= (byte)'Z' => value - 'A',
            >= (byte)'a' and <= (byte)'z' => value - 'a' + 26,
            >= (byte)'0' and <= (byte)'9' => value - '0' + 52,
            (byte)'+' => 62, (byte)'/' => 63, _ => -1
        };
    }

    internal bool TryDecodeUri(JqBudget budget, [NotNullWhen(true)] out JqString? result)
    {
        result = null;
        int length = ByteLength;
        for (int index = 0, scanned = 0; index < ByteLength; index++)
        {
            if ((scanned++ & 4095) == 0) budget.CheckCancellation();
            if (_utf8.Span[index] != (byte)'%') continue;
            if (index + 2 >= ByteLength || Hex(_utf8.Span[index + 1]) < 0 || Hex(_utf8.Span[index + 2]) < 0) return false;
            length -= 2;
            index += 2;
        }
        using var memory = new JqMemoryReservation(budget.MemoryBudget);
        if (length != 0) memory.Resize(length);
        budget.CheckCancellation();
        byte[] decoded = new byte[length];
        int written = 0;
        for (int index = 0, scanned = 0; index < ByteLength; index++)
        {
            if ((scanned++ & 4095) == 0) budget.CheckCancellation();
            byte value = _utf8.Span[index];
            if (value == (byte)'%')
            {
                value = (byte)((Hex(_utf8.Span[index + 1]) << 4) | Hex(_utf8.Span[index + 2]));
                index += 2;
            }
            decoded[written++] = value;
        }
        for (int offset = 0; offset < length;)
        {
            budget.CheckCancellation();
            if (Rune.DecodeFromUtf8(decoded.AsSpan(offset), out _, out int count) != OperationStatus.Done) return false;
            offset += count;
        }
        result = FromUtf8(decoded, budget);
        return true;

        static int Hex(byte value) => value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - '0',
            >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => value - 'A' + 10, _ => -1
        };
    }

    // Base64 decoding replaces malformed UTF-8, as the language requires.
    private static JqString FromDecodedUtf8(ReadOnlyMemory<byte> input, JqBudget budget)
    {
        long length = 0;
        bool valid = true;
        for (int offset = 0; offset < input.Length;)
        {
            budget.CheckCancellation();
            OperationStatus status = Rune.DecodeFromUtf8(input.Span[offset..], out Rune rune, out int count);
            valid &= status == OperationStatus.Done;
            length += rune.Utf8SequenceLength;
            offset += count;
        }
        if (valid) return FromUtf8(input.Span, budget);
        return Create(JqBudget.BufferCapacity(length, 0), (Input: input, Budget: budget), static (output, state) =>
        {
            int written = 0;
            for (int offset = 0; offset < state.Input.Length;)
            {
                state.Budget.CheckCancellation();
                Rune.DecodeFromUtf8(state.Input.Span[offset..], out Rune rune, out int consumed);
                written += rune.EncodeToUtf8(output[written..]);
                offset += consumed;
            }
        }, budget);
    }
}
