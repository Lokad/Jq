using Lokad.Jq.Benchmarking;
using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("min_by", "1\n")]
    [InlineData("max_by", "2\n")]
    [InlineData("sort_by", "[1,2]\n")]
    [InlineData("group_by", "[[1,2]]\n")]
    [InlineData("unique_by", "[1]\n")]
    public async Task Jq_ComparisonKeysDoNotAddJsonDepth(string operation, string expected)
    {
        string key = new string('[', 64) + "0" + new string(']', 64);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("[1,2]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--argjson", "key", key,
            "try " + operation + "($key) catch \"caught\"")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("min_by", "0\n")]
    [InlineData("max_by", "199\n")]
    [InlineData("sort_by", "200\n")]
    [InlineData("group_by", "1\n")]
    [InlineData("unique_by", "1\n")]
    public async Task Jq_ComparisonKeysBorrowLargeValues(string operation, string expected)
    {
        string key = "[" + string.Join(',', Enumerable.Repeat("1", 1000)) + "]";
        var memory = new BenchmarkMemoryBudget(512 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("[" + string.Join(',', Enumerable.Range(0, 200)) + "]");
        string filter = operation + "($key)" + (operation is "min_by" or "max_by" ? "" : " | length");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--argjson", "key", key, filter)));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("sort_by(empty)", "[3,1,2]\n")]
    [InlineData("group_by(empty)", "[[3,1,2]]\n")]
    [InlineData("unique_by(empty)", "[3]\n")]
    [InlineData("min_by(empty)", "3\n")]
    [InlineData("max_by(empty)", "2\n")]
    [InlineData("sort_by((. % 2), -.)", "[2,3,1]\n")]
    [InlineData("group_by(nan)", "[[3],[1],[2]]\n")]
    [InlineData("unique_by(nan)", "[3,1,2]\n")]
    public async Task Jq_ComparisonKeysPreserveEmptyStreamsOrderingAndNan(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[3,1,2]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public void Jq_ComparisonKeysKeepBorrowedValuesAndConstructIndependentOutputs()
    {
        var source = new JsonArray(new JsonArray(2), new JsonArray(1));
        using var budget = new JqBudget(CancellationToken.None);
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), budget);
        var keys = CollectionKeys.Keyed(source, new IdentityFilter(), context, context.RootEnvironment);
        Assert.Same(source[0], keys[0].Keys[0]);
        Assert.Same(source[1], keys[1].Keys[0]);
        var filter = new SortByFilter(new IdentityFilter());
        var output = Assert.IsType<JsonArray>(filter.Evaluate(source, context, context.RootEnvironment).Single());
        Assert.NotSame(source[1], output[0]);
        Assert.NotSame(source[0], output[1]);
        Assert.Same(source, source[0]?.Parent);
        Assert.Same(source, source[1]?.Parent);
    }
}
