using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_StringOrderingMatchesScalarSequences()
    {
        string[] alphabet = ["a", "z", "é", "\ud7ff", "\ue000", "\uffff", "\ud800", "\udbff", "\udc00", "\udfff", "\U00010000", "🚀"];
        var random = new Random(10743);
        var samples = new List<string?> { null, "", "a", "aa", "ab" };
        for (int index = 0; index < 180; index++)
            samples.Add(string.Concat(Enumerable.Range(0, random.Next(1, 12))
                .Select(_ => alphabet[random.Next(alphabet.Length)])));
        foreach (string? left in samples)
            foreach (string? right in samples)
                Assert.Equal(ScalarOrder(left, right), Math.Sign(JqComparer.CompareStrings(left, right, CancellationToken.None)));

        static int ScalarOrder(string? left, string? right)
        {
            if (left is null || right is null) return left is null ? right is null ? 0 : -1 : 1;
            int[] first = left.EnumerateRunes().Select(rune => rune.Value).ToArray();
            int[] second = right.EnumerateRunes().Select(rune => rune.Value).ToArray();
            for (int index = 0; index < Math.Min(first.Length, second.Length); index++)
                if (first[index] != second[index]) return Math.Sign(first[index].CompareTo(second[index]));
            return Math.Sign(first.Length.CompareTo(second.Length));
        }
    }

    [Theory]
    [InlineData(4094)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(8191)]
    public void Jq_StringOrderingPreservesSurrogatesAtLongPrefixBoundaries(int length)
    {
        string prefix = new('a', length);
        Assert.True(JqComparer.CompareStrings(prefix + "🚀", prefix + "🚁", CancellationToken.None) < 0);
        Assert.True(JqComparer.CompareStrings(prefix + "\ue000", prefix + "\U00010000", CancellationToken.None) < 0);
        Assert.Equal(0, JqComparer.CompareStrings(prefix + "\ud800", prefix + "\ud801", CancellationToken.None));
        Assert.True(JqComparer.CompareStrings(prefix + "\ud800a", prefix + "\ud801b", CancellationToken.None) < 0);
        Assert.Equal(0, JqComparer.CompareStrings(prefix, new string('a', length), CancellationToken.None));
    }
}
