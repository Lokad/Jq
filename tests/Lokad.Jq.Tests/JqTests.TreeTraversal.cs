using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Jq_DescentBorrowsWideChildrenWithinSmallAdmission(bool objects, bool paths)
    {
        JsonNode input = WideNullContainer(objects);
        var root = new BenchmarkMemoryBudget(4096);
        var counted = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None)))
        {
            var filter = new RecursiveDescentFilter();
            if (paths)
            {
                JqValuePath[] values = await filter.EvaluatePaths(new JqValuePath([], input, true), context, context.RootEnvironment).Take(2).ToArrayAsync();
                Assert.Equal(2, values.Length);
                Assert.Same(input, values[0].Value);
                Assert.Null(values[1].Value);
                Assert.Equal(objects ? "[\"k0\"]" : "[0]", JqPaths.PathToJson(values[1].Segments, context.Budget).ToJsonString());
            }
            else
            {
                JsonNode?[] values = await filter.Evaluate(input, context, context.RootEnvironment).Take(2).ToArrayAsync();
                Assert.Equal(2, values.Length);
                Assert.Same(input, values[0]);
                Assert.Null(values[1]);
                Assert.Equal(0, root.ReservedBytes);
            }
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 4);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Jq_DescentRefusesBeforeBufferingSiblings(bool objects, bool paths)
    {
        JsonNode input = WideNullContainer(objects);
        var root = new BenchmarkMemoryBudget(0);
        var counted = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None)))
        {
            var filter = new RecursiveDescentFilter();
            await using IAsyncEnumerator<object?> values = paths
                ? filter.EvaluatePaths(new JqValuePath([], input, true), context, context.RootEnvironment).Cast<object?>().GetAsyncEnumerator()
                : filter.Evaluate(input, context, context.RootEnvironment).Cast<object?>().GetAsyncEnumerator();
            Assert.True(await values.MoveNextAsync());
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = await Assert.ThrowsAsync<JqQuotaException>(async () => await values.MoveNextAsync());
            Assert.True(failure.IsMemoryAdmission);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.Equal(1, counted.Counts.Refusals);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_DescentTraversesDeepValuesAndCancelsWithOpenCursors()
    {
        JsonNode? input = null;
        for (int depth = 0; depth < 5000; depth++) input = new JsonArray(input);
        var root = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token)))
        {
            var filter = new RecursiveDescentFilter();
            Assert.Equal(5001, await filter.Evaluate(input, context, context.RootEnvironment).CountAsync());
            Assert.Equal(0, root.ReservedBytes);
            await using IAsyncEnumerator<JsonNode?> values = filter.Evaluate(input, context, context.RootEnvironment).GetAsyncEnumerator();
            Assert.True(await values.MoveNextAsync());
            Assert.True(await values.MoveNextAsync());
            Assert.True(root.ReservedBytes > 0);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await values.MoveNextAsync());
            Assert.Equal(0, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("limit(2; ..)", "ready\nitem\nitem\n")]
    [InlineData("first(paths)", "ready\nitem\n")]
    public async Task Jq_DescentRemainsLazyUnderHostPressure(string operation, string expected)
    {
        const long limit = 8 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/input", WideNullContainer(false).ToJsonString());
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + operation + " | \"item\")", "/input")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 8192, out pressure));
        };
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("limit(2; ..)", "ready\nitem\n")]
    [InlineData("first(paths)", "ready\n")]
    public async Task Jq_DescentRefusalEscapesLanguageHandlersAndClosesInput(string operation, string expected)
    {
        foreach (bool optional in new[] { false, true })
        {
            const long limit = 64 * 1024;
            var root = new BenchmarkMemoryBudget(limit);
            var host = new MockFileSystem();
            host.AddFile("/input", "[null,null]");
            string guarded = optional ? "(" + operation + ")?" : "try (" + operation + ") catch \"caught\"";
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + guarded + " | \"item\")", "/input")));
            IMemoryReservation? pressure = null;
            host.BeforeByteAppend = () =>
            {
                if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 256, out pressure));
            };
            Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
            Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Single(host.ClosedDescriptors);
            Assert.Equal(0, host.OpenFileCount);
            Assert.NotNull(pressure);
            Assert.Equal(pressure.Bytes, root.ReservedBytes);
            pressure.Dispose();
            Assert.Equal(0, root.ReservedBytes);
        }
    }

    private static JsonNode WideNullContainer(bool objects)
    {
        if (objects)
        {
            var obj = new JsonObject();
            for (int index = 0; index < 100000; index++) obj.Add("k" + index, null);
            return obj;
        }
        var array = new JsonArray();
        for (int index = 0; index < 100000; index++) array.Add((JsonNode?)null);
        return array;
    }
}
