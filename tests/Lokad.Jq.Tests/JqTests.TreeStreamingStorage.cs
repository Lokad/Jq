using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_TostreamBorrowsWideContainersBeforeItsFirstEvent(bool objects)
    {
        JsonNode input = WideNullContainer(objects);
        var root = new BenchmarkMemoryBudget(4096);
        var counted = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None)))
        {
            JsonNode? output = await new TostreamFilter().Evaluate(input, context, context.RootEnvironment).FirstAsync();
            Assert.Equal(objects ? "[[\"k0\"],null]" : "[[0],null]", output?.ToJsonString());
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 5);
            Assert.InRange(root.ReservedBytes, 1, 4096);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_TostreamAdmitsDeepDescentBeforeConstructingFrames()
    {
        JsonNode? input = null;
        for (int depth = 0; depth < 5000; depth++) input = new JsonArray(input);
        var root = new BenchmarkMemoryBudget(0);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = await Assert.ThrowsAsync<JqQuotaException>(async () => await new TostreamFilter().Evaluate(input, context, context.RootEnvironment).FirstAsync());
            Assert.True(failure.IsMemoryAdmission);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, root.ReservedBytes);

        root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            var output = Assert.IsType<JsonArray>(await new TostreamFilter().Evaluate(input, context, context.RootEnvironment).FirstAsync());
            var path = Assert.IsType<JsonArray>(output[0]);
            Assert.Equal(5000, path.Count);
            Assert.All(path, segment => Assert.Equal(0L, segment?.GetValue<long>()));
            Assert.Null(output[1]);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_TostreamCancelsWhileDescendingAndRefundsItsCursors()
    {
        JsonNode? input = null;
        for (int depth = 0; depth < 5000; depth++) input = new JsonArray(input);
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var budget = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, budget, cancellation.Token)))
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await new TostreamFilter().Evaluate(input, context, context.RootEnvironment).FirstAsync());
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[1,[2],3]", "[[[0],1],[[1,0],2],[[1,0]],[[2],3],[[2]]]")]
    [InlineData("{\"\":[null,{}],\"z\":[]}", "[[[\"\",0],null],[[\"\",1],{}],[[\"\",1]],[[\"z\"],[]],[[\"z\"]]]")]
    [InlineData("null", "[[[],null]]")]
    [InlineData("[]", "[[[],[]]]")]
    [InlineData("{}", "[[[],{}]]")]
    public async Task Jq_TostreamPreservesClosePathsAndIndependentEvents(string json, string expected)
    {
        JsonNode? input = JsonNode.Parse(json);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            JsonNode?[] values = await new TostreamFilter().Evaluate(input, context, context.RootEnvironment).ToArrayAsync();
            var output = new JsonArray(values);
            Assert.Equal(expected, output.ToJsonString());
            var first = Assert.IsType<JsonArray>(output[0]);
            Assert.IsType<JsonArray>(first[0]).Add("changed");
            Assert.Equal(json, input?.ToJsonString() ?? "null");
            for (int index = 1; index < output.Count; index++)
                Assert.DoesNotContain("changed", output[index]?.ToJsonString() ?? "null", StringComparison.Ordinal);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_TostreamFirstEventFitsHostPressureWithoutKeyInventories(bool objects)
    {
        const long limit = 64 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/input", WideNullContainer(objects).ToJsonString());
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (first(tostream) | \"item\")", "/input")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 8192, out pressure));
        };
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\nitem\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_TostreamRefusalIsTerminalAndClosesInput(bool optional)
    {
        const long limit = 64 * 1024;
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/input", "[[null]]");
        string source = optional ? "tostream?" : "try tostream catch \"caught\"";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + source + ")", "/input")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 256, out pressure));
        };
        Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }
}
