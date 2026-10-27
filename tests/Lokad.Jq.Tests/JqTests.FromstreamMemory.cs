using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_FromstreamBuildsPrivateArrayWithoutCopyingEarlierElements()
    {
        var events = new JsonArray();
        for (int i = 0; i < 2000; i++) events.Add(new JsonArray(new JsonArray(JsonValue.Create(i)), null));
        events.Add(new JsonArray(new JsonArray(JsonValue.Create(1999))));
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None)))
        {
            var filter = new FromstreamFilter(new IteratorFilter(new IdentityFilter(), false));
            var result = Assert.IsType<JsonArray>(await filter.Evaluate(events, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(2000, result.Count);
            Assert.All(result, Assert.Null);
            Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 200);
        }
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(2001, events.Count);
    }

    [Theory]
    [InlineData("fromstream([[],[1,2]],[[0],9],[[0]])", "[1,2]\n[9]\n")]
    [InlineData("{a:[1]} as $v | [fromstream([[], $v], [[\"a\",0],2], [[\"a\"]]), $v]", "[{\"a\":[1]},{\"a\":[2]},{\"a\":[1]}]\n")]
    [InlineData("fromstream([[0,0],1],[[0,1],2],[[0]])", "[[1,2]]\n")]
    [InlineData("fromstream([[\"a\",0],1],[[\"a\",1],2],[[\"a\"]])", "{\"a\":[1,2]}\n")]
    public async Task Jq_FromstreamKeepsInputAliasesAndEmittedSnapshots(string source, string expected)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FromstreamGeneratedArrayFitsSharedAdmission()
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "fromstream((range(2000)|[[.],null]), [[1999]]) | length")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("2000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FromstreamLongInputRecordsReuseTheirOwners()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("[[[0],1],[[1],2],[[1]]]\n", 2000)));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "fromstream(.[])")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("[1,2]\n", 2000)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
