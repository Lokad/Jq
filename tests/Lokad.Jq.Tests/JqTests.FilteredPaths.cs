using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("false", "")]
    [InlineData("empty", "")]
    [InlineData("scalars", "2000\n")]
    public async Task Jq_FilteredPathsKeepRejectedDeepPathsWithinMemory(string predicate, string expected)
    {
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        string source = "null | setpath([range(2000)|0];0) | paths(" + predicate + ") | length";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FilteredPathsKeepOrderMultiplicityAndIndependentResults()
    {
        JsonNode? input = JsonNode.Parse("{\"a\":[0,false],\"b\":null}");
        var root = new BenchmarkMemoryBudget(32 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            var predicate = new JqParser("false,true,true", JqProgramSource.Inline, context.RootEnvironment, context.Budget).Parse();
            JsonNode?[] paths = await new PathsFilter(predicate).Evaluate(input, context, context.RootEnvironment).ToArrayAsync();
            Assert.Equal("[[\"a\"],[\"a\"],[\"a\",0],[\"a\",0],[\"a\",1],[\"a\",1],[\"b\"],[\"b\"]]", new JsonArray(paths).ToJsonString());
            Assert.IsType<JsonArray>(paths[0]).Add("changed");
            Assert.All(paths.Skip(1), path => Assert.DoesNotContain("changed", path?.ToJsonString() ?? "null", StringComparison.Ordinal));
            Assert.Equal("{\"a\":[0,false],\"b\":null}", input?.ToJsonString());
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FilteredPathsCancelDuringRejectedDescentAndRefundCursors()
    {
        JsonNode? input = null;
        for (int depth = 0; depth < 2000; depth++) input = new JsonArray(input);
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new PathsFilter(new LiteralFilter(JsonValue.Create(false))).Evaluate(input, context, context.RootEnvironment).CountAsync());
        Assert.Equal(0, root.ReservedBytes);
    }
}
