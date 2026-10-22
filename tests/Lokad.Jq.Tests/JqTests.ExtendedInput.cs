using System.Text;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(17, true)]
    [InlineData(8192, true)]
    [InlineData(1, false)]
    [InlineData(17, false)]
    [InlineData(8192, false)]
    public async Task Jq_ExtendedInputRetainsCompletedNodesAcrossReads(int chunk, bool leading)
    {
        string zeros = string.Join(',', Enumerable.Repeat("0", 5000));
        string input = leading ? "[nan," + zeros + "]" : "[" + zeros + ",-infinity]";
        var host = new MockFileSystem();
        host.AddFile("/input", input);
        var root = new BenchmarkMemoryBudget(768 * 1024);
        var memory = new CountingMemoryBudget(root);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "length", "/input")));
        Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(host, chunk), memory, CancellationToken.None));
        Assert.Equal("5001\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 600);
    }

    [Theory]
    [InlineData("[NaN,-Inf,+Infinity,0]")]
    [InlineData("[nan, infinity, -nan, +inf]")]
    [InlineData("{\"a\":nan,\"b\":{\"c\":-infinity},\"a\":INF}")]
    [InlineData("[0,{\"a\":nan,\"b\":[inf,0]},infinity]")]
    [InlineData("[nan  ,  0, -inf \n ,\n infinity]")]
    [InlineData("{\"a\":nan  ,  \"b\":0,\"c\":-inf \n }")]
    [InlineData("nan \n")]
    [InlineData("+Infinity\t")]
    [InlineData("-inf")]
    public void Jq_ExtendedInputMatchesWholeValueParsingAcrossEveryBoundary(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        using var budget = new JqBudget(CancellationToken.None);
        var runtime = new JqRuntime(budget);
        JsonNode? expected = runtime.ReadJsonValue(bytes, out int expectedConsumed);
        foreach (int chunk in new[] { 1, 2, 3, 7, bytes.Length })
        {
            var reader = new JqRuntime.InputReader(runtime, budget);
            for (int end = Math.Min(chunk, bytes.Length); ; end = Math.Min(end + chunk, bytes.Length))
            {
                if (reader.TryRead(bytes.AsSpan(0, end), end == bytes.Length, out JsonNode? actual, out int consumed))
                {
                    Assert.Equal(expectedConsumed, consumed);
                    Assert.Equal(0, runtime.Compare(expected, actual));
                    break;
                }
                Assert.True(end < bytes.Length, "Final input must complete.");
            }
        }
    }

    [Theory]
    [InlineData("[nan,0,]")]
    [InlineData("[0,nan,]")]
    [InlineData("{\"a\":nan,\"b\":[2,]}")]
    [InlineData("{\"a\":nan,\"b\":\"\\ud800\"}")]
    [InlineData("[nan,\n1,\n2}")]
    [InlineData("[nan,0")]
    [InlineData("+infinityx")]
    public async Task Jq_ExtendedInputKeepsMalformedDiagnosticsAcrossReads(string input)
    {
        var whole = new MockFileSystem();
        whole.SetStandardInput("1\n" + input);
        var expected = await RunInputAsync(whole, "-c", ".");
        Assert.Equal(5, expected.Exit);
        Assert.Equal("1\n", expected.Out);
        foreach (int chunk in new[] { 1, 3, 17 })
        {
            var host = new MockFileSystem();
            host.SetStandardInput("1\n" + input);
            Assert.Equal(expected, await RunInputAsync(new ChunkedHost(host, chunk), "-c", "."));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ExtendedInputCancellationAndRefusalCloseOnlyTheActiveSource(bool cancel)
    {
        var host = new MockFileSystem();
        host.AddFile("/input", "[nan," + string.Join(',', Enumerable.Repeat("0", 5000)) + "]");
        host.AddFile("/later", "0");
        var memory = new BenchmarkMemoryBudget(cancel ? 768 * 1024 : 32 * 1024);
        using var cancellation = new CancellationTokenSource();
        if (cancel) host.BeforeByteRead = _ => { if (host.ReadBytesCallCount == 8) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try inputs catch \"caught\"", "/input", "/later")));
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(new ChunkedHost(host, 17), memory, cancellation.Token));
        else
        {
            Assert.Equal(5, await command.ExecuteAsync(new ChunkedHost(host, 17), memory, cancellation.Token));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        }
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        if (cancel) Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ExtendedInputStreamsWithinSharedAllowanceAndStopsOnClosure()
    {
        const int records = 2000;
        string record = "[nan,-inf,+Infinity,0]\n";
        foreach (bool close in new[] { false, true })
        {
            var host = new MockFileSystem { AppendRemainsOpen = !close };
            host.AddFile("/input", string.Concat(Enumerable.Repeat(record, records)));
            host.AddFile("/later", "[]");
            var memory = new BenchmarkMemoryBudget(64 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "length", "/input", "/later")));
            Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(host, 17), memory, CancellationToken.None));
            Assert.Equal(close ? "4\n" : string.Concat(Enumerable.Repeat("4\n", records)) + "0\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(close ? 1 : 2, host.ClosedDescriptors.Count);
            Assert.Equal(0, host.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }
}
