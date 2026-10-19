using System;
using System.Text;

namespace Lokad.Jq.Helpers;

internal static class Utf8Text
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Count in bounded chunks so a representable UTF-16 string need not fit an
    // int-sized encoded byte count. Callers materializing bytes check capacity;
    // utf8bytelength can return the long count without allocating a byte buffer.
    internal static long GetByteCount(ReadOnlySpan<char> text, CancellationToken cancellationToken)
    {
        long bytes = 0;
        while (!text.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(64 * 1024, text.Length);
            if (count < text.Length && char.IsHighSurrogate(text[count - 1])) count--;
            bytes += Encoding.UTF8.GetByteCount(text[..count]);
            text = text[count..];
        }
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    public static string Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        return StrictUtf8.GetString(bytes.Span);
    }

    public static ReadOnlyMemory<byte> Encode(string text)
    {
        return string.IsNullOrEmpty(text) ? ReadOnlyMemory<byte>.Empty : StrictUtf8.GetBytes(text);
    }

    public static int Encode(string text, Span<byte> destination) =>
        StrictUtf8.GetBytes(text.AsSpan(), destination);

}
