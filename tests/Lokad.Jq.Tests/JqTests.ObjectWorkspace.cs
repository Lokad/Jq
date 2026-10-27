using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{a:empty}")]
    [InlineData("{a:empty,b:1}")]
    [InlineData("{(\"a\"):empty,b:1}")]
    public async Task Jq_EmptyObjectConstructionReusesWorkspace(string body)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "range(10000) | " + body)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 100);
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ObjectWorkspaceEndsWithoutRefundingRetainedOutput()
    {
        ObjectProperty[] properties = Enumerable.Range(0, 1000)
            .Select(i => new ObjectProperty("k" + i, null, new LiteralFilter(null))).ToArray();
        var filter = new ObjectFilter(properties);
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            context.Budget.BeginRecord();
            var results = filter.Evaluate(null, context, context.RootEnvironment).GetAsyncEnumerator();
            Assert.True(await results.MoveNextAsync());
            var output = Assert.IsType<JsonObject>(results.Current);
            long pending = root.ReservedBytes;
            await results.DisposeAsync();
            Assert.InRange(root.ReservedBytes, 96000, pending - 64000);
            Assert.Equal(1000, output.Count);
            Assert.True(output.ContainsKey("k999"));
            Assert.Null(output["k999"]);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ObjectWorkspaceRefusalRemainsTerminalAfterOutput()
    {
        const long allowance = 8 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(allowance);
        IMemoryReservation? pressure = null;
        var host = new MockFileSystem();
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(allowance - root.ReservedBytes, out pressure));
        };
        string fields = string.Join(',', Enumerable.Range(0, 1000).Select(i => "k" + i + ":empty"));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"ready\", (try {" + fields + "} catch \"caught\")")));
        try
        {
            Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
            Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(Assert.IsAssignableFrom<IMemoryReservation>(pressure).Bytes, root.ReservedBytes);
        }
        finally { pressure?.Dispose(); }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_EmptyObjectLoopCancelsAndClosesOwnedInput()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "null");
        using var cancellation = new CancellationTokenSource();
        host.BeforeByteAppend = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "\"ready\", (while(true; .) | {a:empty,b:1})", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
