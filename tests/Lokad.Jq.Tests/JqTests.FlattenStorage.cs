using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_FlattenWideLeavesNeedOnlyOutputAndAncestorStorage(bool nested)
    {
        var values = new JsonArray();
        for (int index = 0; index < 10000; index++) values.Add((JsonNode?)null);
        JsonNode input = nested ? new JsonArray(values) : values;
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var counted = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None)))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = Assert.IsType<JsonArray>(context.Runtime.Flatten(input, JsonValue.Create(-1)));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(10000, result.Count);
            Assert.All(result, Assert.Null);
            Assert.InRange(allocated, 0, 512 * 1024);
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 140);
            result[0] = 1;
            Assert.Null(values[0]);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_FlattenTraversesDeepArraysIteratively()
    {
        JsonNode input = JsonValue.Create(1);
        for (int depth = 0; depth < 5000; depth++) input = new JsonArray(input);
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
            Assert.Equal("[1]", context.Runtime.Flatten(input, JsonValue.Create(-1)).ToJsonString());
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_FlattenCancelsDuringDescentAndRefundsStorage()
    {
        JsonNode? input = null;
        for (int depth = 0; depth < 5000; depth++) input = new JsonArray(input);
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var budget = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, budget, cancellation.Token)))
            Assert.Throws<OperationCanceledException>(() => context.Runtime.Flatten(input, JsonValue.Create(-1)));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("0", "[1,[2,[3]],4]")]
    [InlineData("1", "[1,2,[3],4]")]
    [InlineData("1.5", "[1,2,3,4]")]
    [InlineData("\"x\"", "\"string (\\\"x\\\") and number (1) cannot be subtracted\"")]
    public async Task Jq_FlattenPreservesDepthAndStagedErrors(string depth, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1,[2,[3]],4]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "try flatten(" + depth + ") catch .")));
        var root = new BenchmarkMemoryBudget(64 * 1024);
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }
}
