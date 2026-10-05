using System;
using System.Text;

namespace Lokad.Jq.Helpers;

internal static class Utf8Text
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly ReadOnlyMemory<byte> Newline = "\n"u8.ToArray();

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

    public static ReadOnlyMemory<byte> EncodeLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Newline;

        var byteCount = StrictUtf8.GetByteCount(text);
        var buffer = new byte[byteCount + 1];
        StrictUtf8.GetBytes(text.AsSpan(), buffer.AsSpan());
        buffer[^1] = (byte)'\n';
        return buffer;
    }

}
