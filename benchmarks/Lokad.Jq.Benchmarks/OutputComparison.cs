using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Lokad.Jq.Benchmarking;

internal static class OutputComparison
{
    public static bool Equivalent(byte[] left, byte[] right, ComparisonKind kind)
    {
        if (kind == ComparisonKind.Bytes) return left.AsSpan().SequenceEqual(right);
        var options = new JsonReaderOptions { AllowMultipleValues = true };
        var leftReader = new Utf8JsonReader(left, options);
        var rightReader = new Utf8JsonReader(right, options);
        while (true)
        {
            bool hasLeft = leftReader.Read();
            bool hasRight = rightReader.Read();
            if (!hasLeft || !hasRight) return hasLeft == hasRight;
            using var leftValue = JsonDocument.ParseValue(ref leftReader);
            using var rightValue = JsonDocument.ParseValue(ref rightReader);
            if (!SameValue(leftValue.RootElement, rightValue.RootElement)) return false;
        }

        static bool SameValue(JsonElement left, JsonElement right)
        {
            if (left.ValueKind != right.ValueKind) return false;
            switch (left.ValueKind)
            {
                case JsonValueKind.Number:
                    if (left.GetRawText() == right.GetRawText()) return true;
                    return CanonicalNumber(left.GetRawText()) == CanonicalNumber(right.GetRawText());
                case JsonValueKind.String: return left.GetString() == right.GetString();
                case JsonValueKind.Array:
                    return left.GetArrayLength() == right.GetArrayLength()
                        && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => SameValue(pair.First, pair.Second));
                case JsonValueKind.Object:
                    var properties = left.EnumerateObject().ToArray();
                    var others = right.EnumerateObject().ToArray();
                    // The catalog has unique keys. Reject duplicates rather than
                    // quietly lose them in a dictionary or conflate their order.
                    return properties.Length == others.Length
                        && properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == properties.Length
                        && others.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == others.Length
                        && properties.All(p => right.TryGetProperty(p.Name, out var value) && SameValue(p.Value, value));
                default: return true;
            }
        }

        static (string Coefficient, BigInteger Power) CanonicalNumber(string raw)
        {
            // JSON grammar was checked by the reader. Normalize decimal spelling
            // exactly, including numbers outside decimal/double precision or range.
            int exponentIndex = raw.IndexOfAny(['e', 'E']);
            string mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
            var power = exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(raw[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
            int dot = mantissa.IndexOf('.');
            if (dot >= 0) power -= mantissa.Length - dot - 1;
            bool negative = mantissa.StartsWith('-');
            string digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal).TrimStart('0');
            if (digits.Length == 0) return ("0", BigInteger.Zero);
            string coefficient = digits.TrimEnd('0');
            power += digits.Length - coefficient.Length;
            return (negative ? "-" + coefficient : coefficient, power);
        }
    }

}
