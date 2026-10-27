using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_AddAppendsIntoItsOwnedArrayWithoutCopyingHistory()
    {
        var fragments = new JsonArray();
        for (int index = 0; index < 1024; index++) fragments.Add(new JsonArray(index));
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var counted = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None))
        {
            var result = Assert.IsType<JsonArray>(await new JqRuntime(budget).AddValues(fragments.ToAsyncEnumerable()));
            Assert.Equal(1024, result.Count);
            for (int index = 0; index < result.Count; index++)
            {
                Assert.Equal(index, result[index]?.GetValue<int>());
                Assert.Equal(index, fragments[index]?[0]?.GetValue<int>());
                Assert.NotSame(result[index], fragments[index]?[0]);
            }
            result[0] = -1;
            Assert.Equal(0, fragments[0]?[0]?.GetValue<int>());
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 100);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[[],[1],null,[2],null,[3]] | add", "[1,2,3]\n")]
    [InlineData("[] | add", "null\n")]
    [InlineData("[null,null] | add", "null\n")]
    [InlineData("[1,null,2,3] | add", "6\n")]
    [InlineData("[\"a\",null,\"b\"] | add", "\"ab\"\n")]
    [InlineData("[{a:1},{a:2,b:3}] | add", "{\"a\":2,\"b\":3}\n")]
    [InlineData("try add(([1],[2],error(\"later\"))) catch .", "\"later\"\n")]
    [InlineData("try ([[],[1],1] | add) catch \"kind error\"", "\"kind error\"\n")]
    [InlineData("[[{x:1}],[{y:2}],[{z:3}]] as $x | ($x|add|.[0].x=4),$x", "[{\"x\":4},{\"y\":2},{\"z\":3}]\n[[{\"x\":1}],[{\"y\":2}],[{\"z\":3}]]\n")]
    public async Task Jq_ArrayAddPreservesKindsNullsErrorsAndSources(string source, string expected)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ArrayAddPreflightsGrowthAndCancelsBeforeMutation(bool cancel)
    {
        var fragment = new JsonArray(1);
        var root = new BenchmarkMemoryBudget(1024);
        using var cancellation = new CancellationTokenSource();
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(async () => await runtime.AddValues(Generate().ToAsyncEnumerable()));
            else Assert.True((await Assert.ThrowsAsync<JqQuotaException>(async () => await runtime.AddValues(Generate().ToAsyncEnumerable()))).IsMemoryAdmission);
            Assert.Single(fragment);
            Assert.Equal(1, fragment[0]?.GetValue<int>());
        }
        Assert.Equal(0, root.ReservedBytes);

        IEnumerable<JsonNode?> Generate()
        {
            for (int index = 0; index < 100; index++)
            {
                if (cancel && index == 3) cancellation.Cancel();
                yield return fragment;
            }
        }
    }
}
