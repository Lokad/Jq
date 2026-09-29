using System;
using System.Buffers;

namespace Lokad.Jq.Helpers;

/// <summary>
/// Common helpers for byte-oriented line content used by shell tools.
/// </summary>
internal static class ByteLines
{
    private const int StackLineOffsetCapacity = 128;

    /// <summary>
    /// Finds up to <paramref name="limit"/> newline-terminated lines and returns their total byte length.
    /// </summary>
    public static int FindCompleteLines(
        ReadOnlySpan<byte> content,
        int limit,
        out int[] lineStartOffsets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        Span<int> offsets = stackalloc int[StackLineOffsetCapacity];
        int[]? rentedOffsets = null;
        var offsetCount = 0;
        try
        {
            var lineCount = 0;
            var consumed = 0;
            while (lineCount < limit)
            {
                var newlineOffset = content[consumed..].IndexOf((byte)'\n');
                if (newlineOffset < 0)
                    break;

                if (lineCount > 0)
                {
                    if (offsetCount == offsets.Length)
                    {
                        var maximumOffsetCount = Math.Min(limit - 1, content.Length);
                        var requestedCapacity = (int)Math.Min(
                            maximumOffsetCount,
                            Math.Max((long)offsetCount + 1, (long)offsets.Length * 2));
                        var replacement = ArrayPool<int>.Shared.Rent(requestedCapacity);
                        offsets.CopyTo(replacement);
                        if (rentedOffsets is not null)
                            ArrayPool<int>.Shared.Return(rentedOffsets);

                        rentedOffsets = replacement;
                        offsets = rentedOffsets;
                    }

                    offsets[offsetCount++] = consumed;
                }

                lineCount++;
                consumed += newlineOffset + 1;
            }

            if (offsetCount == 0)
            {
                lineStartOffsets = Array.Empty<int>();
                return consumed;
            }

            lineStartOffsets = new int[offsetCount];
            offsets[..offsetCount].CopyTo(lineStartOffsets);
            return consumed;
        }
        finally
        {
            if (rentedOffsets is not null)
                ArrayPool<int>.Shared.Return(rentedOffsets);
        }
    }

    /// <summary>
    /// Trims trailing CR and LF bytes from a span.
    /// </summary>
    public static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> content)
    {
        var end = content.Length;
        while (end > 0)
        {
            var value = content[end - 1];
            if (value != (byte)'\n' && value != (byte)'\r')
                break;

            end--;
        }

        return content[..end];
    }

    /// <summary>
    /// Trims trailing CR and LF bytes from a memory block.
    /// </summary>
    public static ReadOnlyMemory<byte> TrimLineEnding(ReadOnlyMemory<byte> content)
    {
        var end = TrimLineEnding(content.Span).Length;
        return content[..end];
    }

    /// <summary>
    /// Ensures the content ends with a newline terminator.
    /// </summary>
    public static ReadOnlyMemory<byte> EnsureNewline(ReadOnlyMemory<byte> content) =>
        EnsureTerminator(content, (byte)'\n');

    /// <summary>
    /// Ensures the content ends with the specified terminator byte.
    /// </summary>
    public static ReadOnlyMemory<byte> EnsureTerminator(ReadOnlyMemory<byte> content, byte terminator)
    {
        if (!content.IsEmpty && content.Span[^1] == terminator)
            return content;

        return AppendTerminator(content, terminator);
    }

    /// <summary>
    /// Appends an LF record terminator, even when the content itself ends with LF.
    /// </summary>
    public static ReadOnlyMemory<byte> AppendNewline(ReadOnlyMemory<byte> content) =>
        AppendTerminator(content, (byte)'\n');

    /// <summary>
    /// Appends a record terminator, even when the content itself ends with that byte.
    /// </summary>
    public static ReadOnlyMemory<byte> AppendTerminator(ReadOnlyMemory<byte> content, byte terminator)
    {
        var buffer = new byte[content.Length + 1];
        content.CopyTo(buffer.AsMemory());
        buffer[^1] = terminator;
        return buffer;
    }
}
