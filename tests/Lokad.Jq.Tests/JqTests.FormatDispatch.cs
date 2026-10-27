using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_FormatDispatchRejectsUnknownNamesWithoutConvertingBorrowedContainers(bool objectInput)
    {
        var array = new JsonArray();
        for (int index = 0; index < 10000; index++) array.Add(index);
        JsonNode input = objectInput ? new JsonObject { ["data"] = array } : array;
        var memory = new BenchmarkMemoryBudget(256);
        var counted = new CountingMemoryBudget(memory);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<JqException>(() => runtime.Format("missing", input));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal("missing is not a valid format", failure.Message);
            Assert.InRange(allocated, 0, 32 * 1024);
            Assert.Equal(58, memory.ReservedBytes);
            Assert.Equal(58, memory.PeakBytes);
            Assert.Equal(1, counted.Counts.Reservations);
            Assert.Equal(0, counted.Counts.Resizes);
            Assert.Equal(10000, array.Count);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_FormatDispatchObservesExistingCancellationBeforeRejectingNames()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, cancellation.Token);
        var runtime = new JqRuntime(budget);
        var failure = Assert.Throws<OperationCanceledException>(() => runtime.Format("missing", null));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Theory]
    [InlineData("try @missing catch \"caught\"", "ready\ncaught\n")]
    [InlineData("@missing?", "ready\n")]
    public async Task Jq_FormatDispatchLargeInputReachesItsLanguageHandler(string operation, string expected)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("[" + string.Join(',', Enumerable.Range(0, 10000)) + "]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-cr", "\"ready\", (" + operation + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("@missing \"literal\"", "literal\n", "")]
    [InlineData("@missing \"before\\(empty)after\"", "", "")]
    [InlineData("try @missing \"\\(error(\"earlier\"))\" catch .", "earlier\n", "")]
    [InlineData("try @missing \"\\((debug|1),2)\" catch .", "missing is not a valid format\n", "[\"DEBUG:\",null]\n")]
    [InlineData("\"ready\", (@missing \"\\(halt_error(7))\")", "ready\n", "")]
    public async Task Jq_FormatDispatchPreservesLazyTemplatesAndEarlierEffects(string filter, string output, string error)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", filter)));
        Assert.Equal(filter.Contains("halt_error", StringComparison.Ordinal) ? 7 : 0,
            await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("try @json catch \"caught\"")]
    [InlineData("@json?")]
    public async Task Jq_FormatDispatchNecessaryConversionRefusalStillTerminates(string operation)
    {
        const long limit = 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.SetStandardInput("[" + string.Join(',', Enumerable.Range(0, 10000)) + "]");
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 4096, out pressure));
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-cr", "\"ready\", (" + operation + ")")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
