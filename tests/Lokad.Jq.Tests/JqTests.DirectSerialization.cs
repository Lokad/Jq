using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Jq_SerializationBorrowsWideArraysAndNormalizesNumbersWhileWriting(bool sorted, bool nonfinite)
    {
        var input = new JsonArray();
        for (int index = 0; index < 10000; index++) input.Add((JsonNode?)null);
        if (nonfinite) input[0] = JsonValue.Create(double.NaN);
        byte[] expected = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("null", 10000)) + "]");
        var root = new BenchmarkMemoryBudget(192 * 1024);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            using (var output = runtime.SerializeUtf8(input, false, null, false, sorted))
                Assert.Equal(expected, output.Value.ToArray());
            Assert.Equal(0, root.ReservedBytes);
            if (nonfinite) Assert.True(double.IsNaN(input[0]?.GetValue<double>() ?? 0));
            else Assert.Null(input[0]);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_SortedSerializationReusesKeyStorageAcrossSiblingObjects()
    {
        var input = new JsonArray();
        for (int index = 0; index < 10000; index++) input.Add(new JsonObject { ["z"] = 1, ["a"] = 2 });
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var counted = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None))
        {
            using (var output = new JqRuntime(budget).SerializeUtf8(input, false, null, false, true))
            {
                using var document = JsonDocument.Parse(output.Value);
                Assert.Equal(10000, document.RootElement.GetArrayLength());
                foreach (JsonElement obj in document.RootElement.EnumerateArray())
                {
                    Assert.Equal(new[] { "a", "z" }, obj.EnumerateObject().Select(property => property.Name));
                    Assert.Equal(2, obj.GetProperty("a").GetInt32());
                    Assert.Equal(1, obj.GetProperty("z").GetInt32());
                }
            }
            Assert.Equal(0, root.ReservedBytes);
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 30);
            Assert.All(input, value => Assert.Equal(new[] { "z", "a" }, Assert.IsType<JsonObject>(value).Select(property => property.Key)));
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_SortedOutputKeepsLongRepeatedBorrowedValuesBounded()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-ncS", "{z:1,a:2} as $x | range(2500) | $x")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, counted, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("{\"a\":2,\"z\":1}\n", 2500)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 80);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_DirectSerializationCancelsDuringBufferAdmission(bool sorted)
    {
        var input = new JsonArray();
        for (int index = 0; index < 100000; index++) input.Add((JsonNode?)null);
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            Assert.Throws<OperationCanceledException>(() => runtime.SerializeUtf8(input, false, null, false, sorted));
            Assert.Equal(0, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_NormalizingANonfiniteLeafPreservesDepthProtection(bool sorted)
    {
        JsonNode? deep = null;
        for (int depth = 0; depth < 64; depth++) deep = new JsonArray(deep);
        var input = new JsonArray(JsonValue.Create(double.NaN), deep);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
        {
            var failure = Assert.Throws<JqQuotaException>(() => new JqRuntime(budget).SerializeUtf8(input, false, null, false, sorted));
            Assert.False(failure.IsMemoryAdmission);
            Assert.Equal("value nesting limit exceeded", failure.Message);
            Assert.True(double.IsNaN(input[0]?.GetValue<double>() ?? 0));
            Assert.Equal(0, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
