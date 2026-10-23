using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_ObjectMergesCopySelectedBranchesOnly(bool recursive)
    {
        var discarded = new JsonArray();
        for (int index = 0; index < 10000; index++) discarded.Add(index);
        var left = new JsonObject { ["drop"] = discarded, ["keep"] = 1 };
        var right = new JsonObject { ["drop"] = 0 };
        var root = new BenchmarkMemoryBudget(4096);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            var result = Assert.IsType<JsonObject>(recursive ? runtime.Multiply(left, right) : runtime.Add(left, right));
            Assert.Equal(new[] { "drop", "keep" }, result.Select(property => property.Key));
            Assert.Equal(0, result["drop"]?.GetValue<int>());
            Assert.Equal(1, result["keep"]?.GetValue<int>());
            Assert.Equal(10000, discarded.Count);
            Assert.Same(discarded, left["drop"]);
            Assert.InRange(root.PeakBytes, 1, 4096);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_RecursiveMergeCopiesTheFinalDeepBranchOnce()
    {
        var data = new JsonArray();
        for (int index = 0; index < 1000; index++) data.Add(index);
        JsonObject left = new() { ["data"] = data };
        JsonObject right = new();
        for (int index = 1; index < 24; index++)
        {
            left = new JsonObject { ["child"] = left };
            right = new JsonObject { ["child"] = right };
        }
        var root = new BenchmarkMemoryBudget(100000);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None))
        {
            var result = Assert.IsType<JsonObject>(new JqRuntime(budget).Multiply(left, right));
            JsonObject leaf = result;
            for (int index = 1; index < 24; index++) leaf = Assert.IsType<JsonObject>(leaf["child"]);
            var copied = Assert.IsType<JsonArray>(leaf["data"]);
            Assert.Equal(1000, copied.Count);
            copied[0] = -1;
            Assert.Equal(0, data[0]?.GetValue<int>());
            Assert.InRange(root.PeakBytes, 88000, 100000);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("+", false)]
    [InlineData("+", true)]
    [InlineData("*", false)]
    [InlineData("*", true)]
    public async Task Jq_MergePressureDistinguishesDiscardedAndRetainedPayloads(string operation, bool retain)
    {
        const long limit = 4 * 1024 * 1024;
        string input = "{\"left\":{\"drop\":[" + string.Join(',', Enumerable.Repeat("null", 10000))
            + "],\"keep\":1},\"right\":" + (retain ? "{\"other\":0}" : "{\"drop\":0}") + "}";
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/input", input);
        string expression = "try ((.left " + operation + " .right) | .keep) catch \"caught\"";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + expression + ")", "/input")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 4096, out pressure));
        };
        Assert.Equal(retain ? 5 : 0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(retain ? "ready\n" : "ready\n1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(retain ? "jq: memory budget exceeded\n" : "", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("+", "{\"z\":0,\"a\":null,\"child\":{\"b\":3},\"new\":4}\n")]
    [InlineData("*", "{\"z\":0,\"a\":null,\"child\":{\"a\":2,\"b\":3},\"new\":4}\n")]
    public async Task Jq_MergePreservesOverwriteRulesAndInsertionOrder(string operation, string expected)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "{z:0,a:1,child:{a:2}} " + operation + " {a:null,child:{b:3},new:4}")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void Jq_MergeRetainsSharedDepthAndCancellationChecks(int depth)
    {
        JsonObject left = new() { ["x"] = 1 };
        JsonObject right = new();
        for (int index = 1; index < depth; index++)
        {
            left = new JsonObject { ["child"] = left };
            right = new JsonObject { ["child"] = right };
        }
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(cancellation.Token);
        var runtime = new JqRuntime(budget);
        if (depth == 64) Assert.IsType<JsonObject>(runtime.Multiply(left, right));
        else Assert.Equal("value nesting limit exceeded", Assert.Throws<JqQuotaException>(() => runtime.Multiply(left, right)).Message);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => runtime.Multiply(left, right));
        Assert.Throws<OperationCanceledException>(() => runtime.Add(left, right));
    }
}
