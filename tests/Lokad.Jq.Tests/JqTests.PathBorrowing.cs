using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_PathVariableReadsBorrowWithoutAdmissionAndStillCancel(bool currentValue)
    {
        var memory = new BenchmarkMemoryBudget(0);
        var value = new JsonArray();
        for (int index = 0; index < 5000; index++) value.Add(index);
        using var cancellation = new CancellationTokenSource();
        using var context = new JqContext(new Dictionary<string, JsonNode?> { ["value"] = value },
            new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token));
        var filter = new VariableFilter("value");
        await using var paths = filter.EvaluatePaths(new JqValuePath([], currentValue ? value : null, true),
            context, context.RootEnvironment).GetAsyncEnumerator();

        Assert.True(await paths.MoveNextAsync());
        Assert.Same(value, paths.Current.Value);
        Assert.Equal(currentValue, paths.Current.Tracked);
        Assert.Equal(0, memory.ReservedBytes);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await paths.MoveNextAsync());
        Assert.Equal(0, memory.ReservedBytes);
    }

    // jq 1.8.2 keeps the variable reference when the nested path starts a new
    // root. Only the outer path result is untracked.
    [Theory]
    [InlineData("\"hi\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"b\":1}")]
    [InlineData("{\"b\":[1,2]}")]
    public async Task Jq_NestedPathPreservesForeignVariableIdentity(string value)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":" + value + "}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            ".a as $x | try path($x | path($x)) catch .")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("\"Invalid path expression with result []\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
