using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

// Native immutable string payload. Construction belongs to the existing budget
// region; aliases share bytes, while a retained slice owns only its selected data.
internal sealed class JqString
{
    private readonly ReadOnlyMemory<byte> _utf8;
    private int _hash;
    private volatile bool _hasHash;

    private JqString(ReadOnlyMemory<byte> utf8, int scalarLength)
    {
        _utf8 = utf8;
        ScalarLength = scalarLength;
    }

    internal ReadOnlyMemory<byte> Utf8 => _utf8;
    internal int ByteLength => _utf8.Length;
    internal int ScalarLength { get; }

    internal static JqString FromUtf8(ReadOnlySpan<byte> utf8, JqBudget budget)
    {
        int scalars = CountScalars(utf8, budget.CancellationToken);
        return Copy(utf8, scalars, budget);
    }

    // Only explicit host/.NET boundaries use UTF-16. Lone surrogates follow the
    // existing host policy and become U+FFFD during this single conversion.
    internal static JqString FromHost(string text, JqBudget budget)
    {
        int length = JqBudget.BufferCapacity(Utf8Text.GetByteCount(text, budget.CancellationToken), 0);
        budget.ReserveBytes(80L + length);
        budget.CheckCancellation();
        byte[] bytes = new byte[length];
        Utf8Text.Encode(text, bytes);
        int scalars = CountScalars(bytes, budget.CancellationToken);
        return new JqString(bytes, scalars);
    }

    internal static JqString FromJson(ref Utf8JsonReader reader, JqBudget budget)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName) || reader.HasValueSequence)
            throw new ArgumentException("A contiguous JSON string token is required.", nameof(reader));
        ReadOnlySpan<byte> source = reader.ValueSpan;
        int length = source.Length;
        if (reader.ValueIsEscaped)
        {
            length = 0;
            while (!source.IsEmpty)
            {
                budget.CheckCancellation();
                int window = Math.Min(65536, source.Length);
                int escape = source[..window].IndexOf((byte)'\\');
                if (escape < 0)
                {
                    length += window;
                    source = source[window..];
                    continue;
                }
                length += escape;
                source = source[escape..];
                if (source[1] != (byte)'u')
                {
                    length++;
                    source = source[2..];
                    continue;
                }
                ushort unit = Hex(source.Slice(2, 4));
                if (unit is >= 0xd800 and <= 0xdbff && source.Length >= 12
                    && source[6] == (byte)'\\' && source[7] == (byte)'u'
                    && Hex(source.Slice(8, 4)) is >= 0xdc00 and <= 0xdfff)
                {
                    length += 4;
                    source = source[12..];
                }
                else
                {
                    length += unit < 0x80 ? 1 : unit < 0x800 ? 2 : 3;
                    source = source[6..];
                }
            }
        }
        // CopyString's escaped-tail check needs one spare destination byte.
        int capacity = JqBudget.BufferCapacity((long)length + (reader.ValueIsEscaped ? 1 : 0), 0);
        budget.ReserveBytes(80L + capacity);
        budget.CheckCancellation();
        byte[] bytes = new byte[capacity];
        try
        {
            int written = reader.CopyString(bytes);
            if (written != length) throw new InvalidOperationException("JSON string size disagrees with its escapes.");
        }
        catch (InvalidOperationException exception) { throw new JqException(exception.Message, exception); }
        int scalars = CountScalars(bytes.AsSpan(0, length), budget.CancellationToken);
        return new JqString(bytes.AsMemory(0, length), scalars);

        static ushort Hex(ReadOnlySpan<byte> digits)
        {
            if (!Utf8Parser.TryParse(digits, out ushort value, out int consumed, 'X') || consumed != 4)
                throw new InvalidOperationException("JSON reader returned an invalid escape.");
            return value;
        }
    }

    internal JqString CloneHeader(JqBudget budget)
    {
        budget.ReserveValueNode();
        return new JqString(_utf8, ScalarLength);
    }

    internal JqString Concat(JqString other, JqBudget budget)
    {
        int length = JqBudget.BufferCapacity((long)ByteLength + other.ByteLength, 0);
        budget.ReserveBytes(80L + length);
        budget.CheckCancellation();
        byte[] bytes = new byte[length];
        _utf8.Span.CopyTo(bytes);
        other._utf8.Span.CopyTo(bytes.AsSpan(ByteLength));
        budget.CheckCancellation();
        return new JqString(bytes, checked(ScalarLength + other.ScalarLength));
    }

    internal JqString Slice(int start, int count, JqBudget budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (start > ScalarLength || count > ScalarLength - start) throw new ArgumentOutOfRangeException(nameof(count));
        int from = ByteOffset(start, budget.CancellationToken);
        int to = Advance(_utf8.Span, from, count, budget.CancellationToken);
        return Copy(_utf8.Span[from..to], count, budget);
    }

    internal static JqString FromScalars(IReadOnlyList<int> points, JqBudget budget)
    {
        long needed = 0;
        for (int index = 0; index < points.Count; index++)
        {
            if ((index & 1023) == 0) budget.CheckCancellation();
            needed += Scalar(points[index]).Utf8SequenceLength;
        }
        int length = JqBudget.BufferCapacity(needed, 0);
        budget.ReserveBytes(80L + length);
        budget.CheckCancellation();
        byte[] bytes = new byte[length];
        int offset = 0;
        for (int index = 0; index < points.Count; index++)
        {
            if ((index & 1023) == 0) budget.CheckCancellation();
            offset += Scalar(points[index]).EncodeToUtf8(bytes.AsSpan(offset));
        }
        budget.CheckCancellation();
        return new JqString(bytes, points.Count);

        static Rune Scalar(int point) => Rune.TryCreate(point, out Rune rune) ? rune : Rune.ReplacementChar;
    }

    internal IEnumerable<int> Scalars(CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < ByteLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rune.DecodeFromUtf8(_utf8.Span[offset..], out Rune rune, out int length);
            offset += length;
            yield return rune.Value;
        }
    }

    internal IEnumerable<JqString> Split(JqString separator, JqBudget budget)
    {
        budget.CheckCancellation();
        if (ByteLength == 0) yield break;
        if (separator.ByteLength == 0)
        {
            int offset = 0;
            while (offset < ByteLength)
            {
                int end = Advance(_utf8.Span, offset, 1, budget.CancellationToken);
                yield return Copy(_utf8.Span[offset..end], 1, budget);
                offset = end;
            }
            yield break;
        }
        int start = 0;
        while (true)
        {
            budget.CheckCancellation();
            int found = Find(separator, start, budget.CancellationToken);
            if (found < 0 && start == 0)
            {
                yield return CloneHeader(budget);
                yield break;
            }
            int end = found < 0 ? ByteLength : found;
            int scalars = CountScalars(_utf8.Span[start..end], budget.CancellationToken);
            yield return Copy(_utf8.Span[start..end], scalars, budget);
            if (found < 0) yield break;
            start = end + separator.ByteLength;
        }
    }

    internal int IndexOf(JqString needle, int start, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (start > ScalarLength) throw new ArgumentOutOfRangeException(nameof(start));
        int offset = ByteOffset(start, cancellationToken);
        int found = Find(needle, offset, cancellationToken);
        return found < 0 ? -1 : start + CountScalars(_utf8.Span[offset..found], cancellationToken);
    }

    internal JqString AsciiCase(bool lower, JqBudget budget)
    {
        budget.CheckCancellation();
        byte from = lower ? (byte)'A' : (byte)'a', to = lower ? (byte)'Z' : (byte)'z';
        if (_utf8.Span.IndexOfAnyInRange(from, to) < 0) return CloneHeader(budget);
        budget.ReserveBytes(80L + ByteLength);
        budget.CheckCancellation();
        byte[] bytes = _utf8.ToArray();
        for (int index = 0; index < bytes.Length; index++)
        {
            if ((index & 4095) == 0) budget.CheckCancellation();
            if (bytes[index] >= from && bytes[index] <= to)
                bytes[index] = (byte)(bytes[index] + (lower ? 32 : -32));
        }
        return new JqString(bytes, ScalarLength);
    }

    internal bool ContentEquals(JqString other, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ByteLength != other.ByteLength) return false;
        for (int offset = 0; offset < ByteLength;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(4096, ByteLength - offset);
            if (!_utf8.Span.Slice(offset, length).SequenceEqual(other._utf8.Span.Slice(offset, length))) return false;
            offset += length;
        }
        return true;
    }

    internal int Compare(JqString other, CancellationToken cancellationToken)
    {
        int shared = Math.Min(ByteLength, other.ByteLength);
        for (int index = 0; index < shared; index += Math.Min(4096, shared - index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(4096, shared - index);
            int order = _utf8.Span.Slice(index, length).SequenceCompareTo(other._utf8.Span.Slice(index, length));
            if (order != 0) return order;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ByteLength.CompareTo(other.ByteLength);
    }

    internal int ContentHashCode(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hasHash) return _hash;
        var hash = new HashCode();
        for (int offset = 0; offset < ByteLength;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(4096, ByteLength - offset);
            hash.AddBytes(_utf8.Span.Slice(offset, length));
            offset += length;
        }
        _hash = hash.ToHashCode();
        _hasHash = true;
        return _hash;
    }

    internal void WriteTo(Utf8JsonWriter writer) => writer.WriteStringValue(_utf8.Span);

    internal int ByteOffset(int scalarOffset, CancellationToken cancellationToken)
    {
        if ((uint)scalarOffset > (uint)ScalarLength) throw new ArgumentOutOfRangeException(nameof(scalarOffset));
        return Advance(_utf8.Span, 0, scalarOffset, cancellationToken);
    }

    // Bound candidate starts per search, while retaining enough subject bytes
    // for a separator that crosses the window boundary.
    private int Find(JqString needle, int start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (needle.ByteLength == 0) return start;
        int last = ByteLength - needle.ByteLength;
        while (start <= last)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(65536, last - start + 1);
            int found = _utf8.Span.Slice(start, needle.ByteLength + count - 1).IndexOf(needle._utf8.Span);
            cancellationToken.ThrowIfCancellationRequested();
            if (found >= 0) return start + found;
            start += count;
        }
        return -1;
    }

    private static JqString Copy(ReadOnlySpan<byte> utf8, int scalars, JqBudget budget)
    {
        JqBudget.BufferCapacity(utf8.Length, 0);
        budget.ReserveBytes(80L + utf8.Length);
        budget.CheckCancellation();
        byte[] bytes = utf8.ToArray();
        budget.CheckCancellation();
        return new JqString(bytes, scalars);
    }

    private static int CountScalars(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        int scalars = 0;
        while (!bytes.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> block = bytes[..Math.Min(4096, bytes.Length)];
            int nonAscii = block.IndexOfAnyExceptInRange((byte)0, (byte)127);
            int ascii = nonAscii < 0 ? block.Length : nonAscii;
            scalars += ascii;
            bytes = bytes[ascii..];
            if (bytes.IsEmpty) break;
            if (nonAscii < 0) continue;
            int walked = 0;
            while (!bytes.IsEmpty && bytes[0] >= 128 && walked < 4096)
            {
                if (Rune.DecodeFromUtf8(bytes, out _, out int length) != OperationStatus.Done)
                    throw new JqException("Invalid UTF-8 string.");
                scalars++;
                walked += length;
                bytes = bytes[length..];
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return scalars;
    }

    private static int Advance(ReadOnlySpan<byte> bytes, int offset, int scalars, CancellationToken cancellationToken)
    {
        for (int index = 0; index < scalars; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            byte first = bytes[offset];
            offset += first < 0x80 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return offset;
    }
}
