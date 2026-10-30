using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_WalkAdmitsWideOutputWithoutCopiedChildInventories()
    {
        var input = new JqArray();
        for (int index = 0; index < 10000; index++) input.Add((JqValue?)null);
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var counted = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None)))
        {
            var output = Assert.IsType<JqArray>(await new WalkFilter(new IdentityFilter()).Evaluate(input, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(10000, output.Count);
            Assert.All(output, Assert.Null);
            output[0] = 1;
            Assert.Null(input[0]);
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 130);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_WalkBorrowsObjectChildCursorsAndPreservesKeyOrder()
    {
        var input = new JqObject(NativeFixtures.Budget());
        for (int index = 0; index < 1000; index++) input.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        var root = new BenchmarkMemoryBudget(128 * 1024);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            var output = Assert.IsType<JqObject>(await new WalkFilter(new IdentityFilter()).Evaluate(input, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(input.Select(property => property.Key.GetValue<string>()), output.Select(property => property.Key.GetValue<string>()));
            output["0"] = 1;
            Assert.Null(input["0"]);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[1,2]", "walk(if type == \"number\" then debug | (.,.+10) else debug end)", "[1,11,2,12]\n", "[\"DEBUG:\",1]\n[\"DEBUG:\",2]\n[\"DEBUG:\",[1,11,2,12]]\n")]
    [InlineData("{\"a\":1,\"b\":2}", "walk(if type == \"number\" then (.,error(\"unused\")) else . end)", "{\"a\":1,\"b\":2}\n", "")]
    [InlineData("[1,2]", "try walk(if type == \"number\" then (.,error(\"later\")) else . end) catch .", "\"later\"\n", "")]
    [InlineData("[1,{\"a\":2},[3]]", "walk(if type == \"number\" then empty else . end)", "[{},[]]\n", "")]
    [InlineData("{\"a\":[1]}", ". as $x | (walk(.) | .a[0] = 2),$x", "{\"a\":[2]}\n{\"a\":[1]}\n", "")]
    public async Task Jq_WalkStreamsChildResultsWithBottomUpEffects(string input, string source, string expected, string error)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_WalkCancelsBetweenChildrenAndRefundsItsOpenFrames()
    {
        var input = new JqObject(NativeFixtures.Budget()) { ["a"] = new JqArray(1, 2, 3), ["b"] = new JqArray(4, 5, 6) };
        using var cancellation = new CancellationTokenSource();
        int visits = 0;
        var body = new WalkVisitFilter(() => { if (++visits == 2) cancellation.Cancel(); });
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, root, cancellation.Token)))
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await new WalkFilter(body).Evaluate(input, context, context.RootEnvironment).SingleAsync());
        Assert.Equal(2, visits);
        Assert.Equal("{\"a\":[1,2,3],\"b\":[4,5,6]}", input.ToJsonString());
        Assert.Equal(0, root.ReservedBytes);
    }

    private sealed class WalkVisitFilter(Action visit) : JqFilter
    {
        protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
        {
            visit();
            yield return input;
        }
    }
}
