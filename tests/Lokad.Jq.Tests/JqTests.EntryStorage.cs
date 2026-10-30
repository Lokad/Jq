using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_WithEntriesBorrowsMappedValuesAndCopiesTheFinalPayloadOnce()
    {
        var payload = new JqArray();
        for (int index = 0; index < 1000; index++) payload.Add(index);
        var input = new JqObject(NativeFixtures.Budget()) { ["payload"] = payload };
        var root = new BenchmarkMemoryBudget(200000);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            var result = Assert.IsType<JqObject>(await new WithEntriesFilter(new IdentityFilter()).Evaluate(input, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(1000, Assert.IsType<JqArray>(result["payload"]).Count);
            result["payload"]?[0] = -1;
            Assert.Equal(0, payload[0]?.GetValue<int>());
            Assert.InRange(root.PeakBytes, 176000, 200000);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try with_entries(if .key == \"a\" then 0 else debug end) catch \"caught\"", "\"caught\"\n", "[\"DEBUG:\",{\"key\":\"b\",\"value\":2}]\n")]
    [InlineData("with_entries(select(.key != \"a\"))", "{\"b\":2}\n", "")]
    [InlineData("with_entries(empty)", "{}\n", "")]
    [InlineData("with_entries(.,.)", "{\"a\":1,\"b\":2}\n", "")]
    [InlineData("with_entries({Key:.key,Value:.value})", "{\"a\":1,\"b\":2}\n", "")]
    [InlineData("with_entries({key:false,Name:.key,value:null,Value:9})", "{\"a\":null,\"b\":null}\n", "")]
    [InlineData("with_entries({name:\"x\",value:.value})", "{\"x\":2}\n", "")]
    [InlineData("try with_entries(if .key == \"a\" then empty else error(\"later\") end) catch .", "\"later\"\n", "")]
    public async Task Jq_WithEntriesPreservesBorrowedEntryRulesAndValidationOrder(string source, string expected, string error)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":1,\"b\":2}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_WithEntriesCancelsBeforeRetainingAnotherMappedValue()
    {
        var input = new JqObject(NativeFixtures.Budget()) { ["a"] = 1, ["b"] = 2 };
        using var cancellation = new CancellationTokenSource();
        int visits = 0;
        var body = new WalkVisitFilter(() => { if (++visits == 2) cancellation.Cancel(); });
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, root, cancellation.Token)))
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await new WithEntriesFilter(body).Evaluate(input, context, context.RootEnvironment).SingleAsync());
        Assert.Equal(2, visits);
        Assert.Equal("{\"a\":1,\"b\":2}", input.ToJsonString());
        Assert.Equal(0, root.ReservedBytes);
    }
}
