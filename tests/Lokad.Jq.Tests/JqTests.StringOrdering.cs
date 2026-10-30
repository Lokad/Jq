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
        using var budget = new JqBudget(CancellationToken.None);
        var runtime = new JqRuntime(budget);
        JqString?[] native = samples.Select(text => text is null ? null : JqString.FromHost(text, budget)).ToArray();
        for (int left = 0; left < samples.Count; left++)
            for (int right = 0; right < samples.Count; right++)
                Assert.Equal(ScalarOrder(samples[left], samples[right]), Math.Sign(runtime.Compare(native[left], native[right])));

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
    public void Jq_NativeStringOrderingUsesScalarOrderAtLongPrefixes(int length)
    {
        using var budget = new JqBudget(CancellationToken.None);
        string prefix = new('a', length);
        Assert.True(Compare(prefix + "🚀", prefix + "🚁") < 0);
        Assert.True(Compare(prefix + "\ue000", prefix + "\U00010000") < 0);
        Assert.Equal(0, Compare(prefix + "\ud800", prefix + "\ud801"));
        Assert.True(Compare(prefix + "\ud800a", prefix + "\ud801b") < 0);
        Assert.Equal(0, Compare(prefix, new string('a', length)));

        int Compare(string left, string right) =>
            JqString.FromHost(left, budget).Compare(JqString.FromHost(right, budget), CancellationToken.None);
    }
}
