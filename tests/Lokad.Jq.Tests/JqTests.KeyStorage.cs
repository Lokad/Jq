using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_KeysPreserveUnicodeOrderAndIndependentOutput(bool sorted)
    {
        string[] inserted = ["😀", "\ue000", "z", "a", "é", "𐀀"];
        string[] expected = sorted ? ["a", "z", "é", "\ue000", "𐀀", "😀"] : inserted;
        var input = new JsonObject();
        foreach (string name in inserted) input.Add(name, new JsonArray(1));
        var root = new BenchmarkMemoryBudget(4096);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
        {
            var output = Assert.IsType<JsonArray>(new JqRuntime(budget).Keys(input, sorted));
            Assert.Equal(expected, output.Select(item => item?.GetValue<string>()));
            output[0] = "changed";
            Assert.Equal(inserted, input.Select(property => property.Key));
            Assert.All(input, property => Assert.Same(input, property.Value?.Parent));
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_KeysPreflightOutputAndOrderingScratch(bool sorted)
    {
        var input = WideObject(64);
        var root = new BenchmarkMemoryBudget(64 + 88 * 64);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            if (sorted)
                Assert.True(Assert.Throws<JqQuotaException>(() => runtime.Keys(input, true)).IsMemoryAdmission);
            else
                Assert.Equal(64, Assert.IsType<JsonArray>(runtime.Keys(input, false)).Count);
            Assert.Equal(64, input.Count);
            Assert.Equal(64 + 88 * 64, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_KeysCancelAfterConstructionAdmission(bool sorted)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token))
            Assert.ThrowsAny<OperationCanceledException>(() => new JqRuntime(budget).Keys(WideObject(64), sorted));
        Assert.Equal(0, root.ReservedBytes);
    }
}
