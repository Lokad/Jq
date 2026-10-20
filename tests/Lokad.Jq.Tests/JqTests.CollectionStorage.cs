using Lokad.Jq.Benchmarking;
using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_UniqueCopiesOnlyRetainedTreesAndLeavesInputIndependent()
    {
        var input = new JsonArray();
        for (int index = 0; index < 100; index++)
        {
            var item = new JsonArray();
            for (int value = 0; value < 1000; value++) item.Add(value);
            input.Add(item);
        }
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            var output = Assert.IsType<JsonArray>(new JqRuntime(budget).UniqueArray(input));
            Assert.Single(output);
            Assert.True(JsonNode.DeepEquals(input[0], output[0]));
            Assert.NotSame(input[0], output[0]);
            Assert.Equal(100, input.Count);
            Assert.All(input, item => Assert.Same(input, item?.Parent));
            Assert.IsType<JsonArray>(output[0])[0] = -1;
            Assert.Equal(0, input[0]?[0]?.GetValue<int>());
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("min_by", "1\n")]
    [InlineData("max_by", "3\n")]
    [InlineData("sort_by", "[1,2,3]\n")]
    [InlineData("group_by", "[[1],[2],[3]]\n")]
    [InlineData("unique_by", "[1,2,3]\n")]
    public async Task Jq_KeyedCollectionsPreserveEvaluationEffects(string operation, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[3,1,2]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", operation + "(debug)")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",3]\n[\"DEBUG:\",1]\n[\"DEBUG:\",2]\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("min_by")]
    [InlineData("max_by")]
    public async Task Jq_IncrementalExtremaStillEvaluateLaterKeyErrors(string operation)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[0,1,2]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "try " + operation + "(if . == 2 then error(\"later\") else . end) catch .")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"later\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
