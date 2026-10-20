using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("setpath([]; $value)")]
    [InlineData(". = $value")]
    [InlineData(". |= $value")]
    [InlineData("fromstream($events[])")]
    public void Jq_PathWritesCopyBorrowedReplacementsAtOneBoundary(string source)
    {
        var value = new JsonArray();
        for (int index = 0; index < 1000; index++) value.Add(index);
        var variables = new Dictionary<string, JsonNode?>
        {
            ["value"] = value,
            ["events"] = new JsonArray(new JsonArray(new JsonArray(), value.DeepClone()))
        };
        using var lexical = new JqBudget(CancellationToken.None);
        var root = new BenchmarkMemoryBudget(100000);
        using (var context = new JqContext(variables, new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            JqFilter filter = new JqParser(source, JqProgramSource.Inline, context.RootEnvironment, lexical).Parse();
            var result = Assert.IsType<JsonArray>(filter.Evaluate(null, context, context.RootEnvironment).Single());
            Assert.Equal(1000, result.Count);
            Assert.NotSame(value, result);
            Assert.Equal(999, result[999]?.GetValue<int>());
            result[0] = -1;
            Assert.Equal(0, value[0]?.GetValue<int>());
            Assert.InRange(root.PeakBytes, 88000, 100000);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[1,2] | [setpath([0]; (3,4))]", "[[3,2],[4,2]]\n")]
    [InlineData("[[1],[2]] | .[] += [3]", "[[1,3],[2,3]]\n")]
    [InlineData("[1,2] | .[] |= (.+1, error(\"unused\"))", "[2,3]\n")]
    [InlineData("{a:1,b:2} | .a |= empty", "{\"b\":2}\n")]
    [InlineData("null | fromstream(([[\"a\"],1], [[\"a\"]]))", "{\"a\":1}\n")]
    public async Task Jq_PathWriteCopyRemovalPreservesGeneratorsAndEmptyUpdates(string source, string expected)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("{\"b\":1}")]
    [InlineData("\"hi\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task Jq_GetpathPreservesBorrowedRootAndChildAliasIdentity(string value)
    {
        foreach (bool child in new[] { false, true })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(child ? "{\"a\":" + value + "}" : value);
            string filter = child ? "getpath([\"a\"]) as $x | .a | path($x)" : "getpath([]) as $x | path($x)";
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
            Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Theory]
    [InlineData("getpath([\"a\"]) as $x | .b | try path($x) catch .", "\"Invalid path expression with result [1]\"\n")]
    [InlineData("path(getpath([\"a\"]))", "[\"a\"]\n")]
    [InlineData("getpath([\"a\"]) as $x | .a[0] = 2 | $x", "[1]\n")]
    [InlineData("getpath([{start:0,end:1}])", "[[1]]\n")]
    public async Task Jq_GetpathRetainsTrackingAndIndependentConstructedSlices(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(filter.Contains("start:", StringComparison.Ordinal) ? "[[1],[2]]" : "{\"a\":[1],\"b\":[1]}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public void Jq_GetpathBorrowsLargeValuesAndAdmitsOnlyPathConstruction()
    {
        var value = new JsonArray();
        for (int index = 0; index < 10000; index++) value.Add(index);
        JsonNode input = new JsonObject { ["payload"] = value };
        var root = new BenchmarkMemoryBudget(64);
        using var cancellation = new CancellationTokenSource();
        var variables = new Dictionary<string, JsonNode?> { ["path"] = new JsonArray("payload") };
        using (var context = new JqContext(variables, new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token)))
        {
            var filter = new GetpathBuiltinFilter(new VariableFilter("path"));
            Assert.Same(value, filter.Evaluate(input, context, context.RootEnvironment).Single());
            Assert.Equal(64, root.ReservedBytes);
            Assert.Same(input, JqPathReads.GetPath(input, Array.Empty<JqValueSegment>(), context));
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => JqPathReads.GetPath(input, Array.Empty<JqValueSegment>(), context));
            Assert.Throws<OperationCanceledException>(() => filter.Evaluate(input, context, context.RootEnvironment).Single());
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GetpathCopiesArraySliceOnceAndKeepsSourceIndependent()
    {
        JsonNode input = new JsonArray(new JsonObject { ["x"] = 1 }, new JsonObject { ["x"] = 2 });
        var variables = new Dictionary<string, JsonNode?>
        {
            ["path"] = new JsonArray(new JsonObject { ["start"] = 0, ["end"] = 1 })
        };
        using var context = new JqContext(variables, new JqBudget(CancellationToken.None));
        var result = Assert.IsType<JsonArray>(new GetpathBuiltinFilter(new VariableFilter("path"))
            .Evaluate(input, context, context.RootEnvironment).Single());
        Assert.Single(result);
        result[0]?["x"] = 9;
        Assert.Equal(1, input[0]?["x"]?.GetValue<int>());
    }
}
